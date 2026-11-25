using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MimeKit;
using MimeKit.IO;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Exceptions;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

internal sealed class EasClient : IDisposable
{
    private const int MaximumResponseBytes = 64 * 1024 * 1024;
    private const string PolicyType = "MS-EAS-Provisioning-WBXML";

    // Newest first. 16.x adds server drafts and server-sent meeting mail; callers branch on IsAtLeast.
    private static readonly string[] SupportedVersions = ["16.1", "16.0", "14.1", "14.0", "12.1", "12.0"];

    private static readonly ILogger Logger = Log.ForContext<EasClient>();
    private readonly HttpClient _http;
    private readonly Lazy<HttpClient> _longPoll;
    private readonly MailAccount _account;
    private readonly Uri _configuredEndpoint;
    private readonly string _user;
    private readonly EasAccountStateStore? _store;
    private readonly string? _deviceIdOverride;
    private readonly EasAccountData? _accountData;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _provisionGate = new(1, 1);
    private EasAccountState? _state;
    private Uri _endpoint;
    private string? _version;

    /// <param name="store">Persists the device partnership; null keeps it in memory, as a connection probe does.</param>
    /// <param name="accountData">Carries out what the server asks of the local data; null for a connection check, which has none.</param>
    /// <param name="deviceId">A device id for a probe that must not touch the live partnership's sync state.</param>
    internal EasClient(MailAccount account, string endpoint, string user, string password, EasAccountStateStore? store,
        EasAccountData? accountData = null, string? deviceId = null, HttpMessageHandler? handler = null)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _configuredEndpoint = NormalizeEndpoint(endpoint)
            ?? throw new ArgumentException("An HTTPS EAS endpoint without credentials, query or fragment is required.", nameof(endpoint));

        if (string.IsNullOrWhiteSpace(user) || user.Contains(':') || string.IsNullOrEmpty(password))
            throw new ArgumentException("An EAS username and password are required.");

        _endpoint = _configuredEndpoint;
        _user = user;
        _store = store;
        _deviceIdOverride = deviceId;
        _accountData = accountData;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinoMail/2.0");

        // Ping holds a request open for minutes; its own client keeps it off the connections that synchronization uses.
        var authorization = _http.DefaultRequestHeaders.Authorization;
        _longPoll = new Lazy<HttpClient>(() =>
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.Authorization = authorization;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WinoMail/2.0");
            return client;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The negotiated MS-ASProtocolVersion, or null before the first request.</summary>
    internal string? ProtocolVersion => _version;

    /// <summary>The negotiated version, else the one stored by an earlier session, so the check needs no request.</summary>
    internal async Task<string?> GetKnownVersionAsync()
    {
        if (_version != null)
            return _version;
        var stored = (await EnsureStateAsync().ConfigureAwait(false)).ProtocolVersion;
        return string.IsNullOrEmpty(stored) ? null : stored;
    }

    internal string? DeviceId => _state?.DeviceId;

    internal string? PolicyKey => _state?.PolicyKey;

    internal Uri Endpoint => _endpoint;

    /// <summary>The synchronization limits of the device policy the current policy key was issued for.</summary>
    internal EasPolicyLimits PolicyLimits => _state is { IsPolicyRecorded: true } state
        ? new EasPolicyLimits(state.PolicyMaxEmailAgeFilter, state.PolicyMaxCalendarAgeFilter, state.PolicyMaxAttachmentSize,
            state.PolicyMaxEmailBodyTruncationSize, state.PolicyMaxEmailHtmlBodyTruncationSize, state.PolicyAttachmentsDisabled)
        : EasPolicyLimits.None;

    internal bool IsAtLeast(string version)
        => _version != null && Version.Parse(_version) >= Version.Parse(version);

    internal Task ConnectAsync(CancellationToken cancellationToken) => ConnectAsync(cancellationToken, true);

    private async Task ConnectAsync(CancellationToken cancellationToken, bool allowRedirect)
    {
        var state = await EnsureStateAsync().ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Options, _endpoint);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (allowRedirect && await TryFollowMailboxRedirectAsync(response).ConfigureAwait(false))
        {
            await ConnectAsync(cancellationToken, false).ConfigureAwait(false);
            return;
        }
        EnsureHttpSuccess(response);

        if (!response.Headers.TryGetValues("MS-ASProtocolVersions", out var advertised))
            throw new InvalidDataException("The server did not advertise EAS protocol versions.");

        var versions = advertised.SelectMany(value => value.Split(',')).Select(value => value.Trim()).ToHashSet(StringComparer.Ordinal);
        _version = SupportedVersions.FirstOrDefault(versions.Contains)
            ?? throw new NotSupportedException(string.Format(Translator.ExchangeSetup_ActiveSyncVersionUnsupported, string.Join(", ", versions)));

        var versionChanged = !string.IsNullOrEmpty(state.ProtocolVersion) && state.ProtocolVersion != _version;
        if (state.ProtocolVersion != _version)
        {
            if (versionChanged)
                Logger.Information("EAS protocol version for account {AccountId} changed from {Previous} to {Version}.", _account.Id, state.ProtocolVersion, _version);
            state.ProtocolVersion = _version;
            await SaveStateAsync().ConfigureAwait(false);
        }

        // The limits the client enforces come with the policy, so a partnership whose policy was never recorded
        // provisions before its first command.
        if (!state.IsPolicyRecorded)
            await ProvisionAsync(state.PolicyKey, cancellationToken).ConfigureAwait(false);

        // From 14.1 the device information travels in the initial Provision request; older versions send it once with
        // Settings, after provisioning and before the first FolderSync.
        if (!IsAtLeast("14.1") && !state.IsDeviceInformationSent)
            await SendDeviceInformationAsync(state, cancellationToken).ConfigureAwait(false);

        // Sync keys and server ids were issued under the previous version; the partnership is synchronized again.
        if (versionChanged)
            await DiscardSynchronizationStateAsync().ConfigureAwait(false);
    }

    /// <summary>Discards the account's synchronized data and sync state, then stops the running operation so it starts over.</summary>
    private async Task DiscardSynchronizationStateAsync()
    {
        if (_accountData == null)
            return;

        Logger.Warning("Discarding the ActiveSync synchronization state of account {AccountId}; the mailbox is synchronized again.", _account.Id);
        await _accountData.DiscardSynchronizationStateAsync().ConfigureAwait(false);
        throw new EasResyncRequiredException(Translator.ExchangeSetup_ActiveSyncResyncRequired);
    }

    private async Task SendDeviceInformationAsync(EasAccountState state, CancellationToken cancellationToken)
    {
        XNamespace settings = "Settings:";
        var response = await PostAsync("Settings", EasWbxml.Encode(new XElement(settings + "Settings",
            new XElement(settings + "DeviceInformation", DeviceInformationSet()))), "application/vnd.ms-sync.wbxml", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The EAS Settings response was empty.");
        RequireSuccess(response);
        RequireSuccess(response.Element(settings + "DeviceInformation") ?? throw new InvalidDataException("The EAS Settings response has no device information status."));
        state.IsDeviceInformationSent = true;
        await SaveStateAsync().ConfigureAwait(false);
    }

    /// <summary>The device properties for Settings or Provision; UserAgent exists from 12.1.</summary>
    private XElement DeviceInformationSet()
    {
        XNamespace settings = "Settings:";
        var set = new XElement(settings + "Set",
            new XElement(settings + "Model", "Wino Mail"),
            new XElement(settings + "OS", Environment.OSVersion.VersionString));
        if (IsAtLeast("12.1"))
            set.Add(new XElement(settings + "UserAgent", "WinoMail/2.0"));
        return set;
    }

    internal async Task<XElement?> CommandAsync(string command, XElement document, CancellationToken cancellationToken)
    {
        if (_version == null)
            await ConnectAsync(cancellationToken).ConfigureAwait(false);

        return await PostAsync(command, EasWbxml.Encode(document), "application/vnd.ms-sync.wbxml", cancellationToken).ConfigureAwait(false);
    }

    internal async Task SendMailAsync(byte[] mime, string clientId, CancellationToken cancellationToken)
    {
        if (_version == null)
            await ConnectAsync(cancellationToken).ConfigureAwait(false);

        XElement? result;
        if (_version is "12.0" or "12.1")
            result = await PostAsync("SendMail&SaveInSent=T", mime, "message/rfc822", cancellationToken).ConfigureAwait(false);
        else
        {
            XNamespace compose = "ComposeMail:";
            var request = new XElement(compose + "SendMail",
                new XElement(compose + "ClientId", clientId),
                new XElement(compose + "SaveInSentItems"),
                new XElement(compose + "Mime", new XAttribute(EasWbxml.Opaque, "base64"), Convert.ToBase64String(mime)));
            result = await CommandAsync("SendMail", request, cancellationToken).ConfigureAwait(false);
        }

        // 118: a message with this ClientId was already sent, so a retry after a lost response is a success.
        if (result != null && StatusOf(result) != "118")
            RequireSuccess(result);
    }

    /// <summary>
    /// One Ping (MS-ASCMD 2.2.1.13) on the long-poll client. The server answers when a folder changes or the heartbeat
    /// ends; <paramref name="timeout"/> bounds the wait beyond that, for a connection that silently died.
    /// </summary>
    internal async Task<XElement> PingAsync(XElement request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_version == null)
            await ConnectAsync(cancellationToken).ConfigureAwait(false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await PostAsync("Ping", EasWbxml.Encode(request), "application/vnd.ms-sync.wbxml", deadline.Token, http: _longPoll.Value).ConfigureAwait(false)
                ?? throw new InvalidDataException("The EAS Ping response was empty.");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The EAS server did not answer a Ping within " + timeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + " s.", exception);
        }
    }

    /// <summary>Settings UserInformation Get (MS-ASCMD 2.2.1.18): the mailbox's addresses and, from 14.1, its display name.</summary>
    internal async Task<XElement> GetUserInformationAsync(CancellationToken cancellationToken)
    {
        XNamespace settings = "Settings:";
        var response = await CommandAsync("Settings", new XElement(settings + "Settings",
            new XElement(settings + "UserInformation", new XElement(settings + "Get"))), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The EAS Settings response was empty.");
        RequireSuccess(response);
        var information = response.Element(settings + "UserInformation")
            ?? throw new InvalidDataException("The EAS Settings response has no user information.");
        RequireSuccess(information);
        return information;
    }

    /// <summary>
    /// ResolveRecipients with a picture request (14.1 and later) for one address. Returns the response's first
    /// recipient, or null when the directory has no entry for the address.
    /// </summary>
    internal async Task<XElement?> ResolveRecipientPictureAsync(string address, int maxSize, CancellationToken cancellationToken)
    {
        XNamespace resolve = "ResolveRecipients:";
        var response = await CommandAsync("ResolveRecipients", new XElement(resolve + "ResolveRecipients",
            new XElement(resolve + "To", address),
            new XElement(resolve + "Options",
                new XElement(resolve + "MaxAmbiguousRecipients", "1"),
                new XElement(resolve + "Picture",
                    new XElement(resolve + "MaxSize", maxSize.ToString(CultureInfo.InvariantCulture)),
                    new XElement(resolve + "MaxPictures", "1")))), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The EAS ResolveRecipients response was empty.");
        RequireSuccess(response);
        var answer = response.Element(resolve + "Response");
        if (answer == null || StatusOf(answer) is "2" or "4")
            return null;
        RequireSuccess(answer);
        return answer.Element(resolve + "Recipient");
    }

    private async Task<XElement?> PostAsync(string command, byte[] bytes, string contentType, CancellationToken cancellationToken,
        bool allowProvision = true, bool allowRedirect = true, HttpClient? http = null, string? policyKey = null)
    {
        var state = await EnsureStateAsync().ConfigureAwait(false);
        var sentPolicyKey = policyKey ?? state.PolicyKey;
        using var request = CreateRequest(command, bytes, contentType, state.DeviceId, sentPolicyKey);
        using var response = await (http ?? _http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (allowRedirect && await TryFollowMailboxRedirectAsync(response).ConfigureAwait(false))
            return await PostAsync(command, bytes, contentType, cancellationToken, allowProvision, false, http, policyKey).ConfigureAwait(false);

        if ((int)response.StatusCode == 449)
        {
            if (!allowProvision)
                throw new EasPolicyException(Translator.ExchangeSetup_ActiveSyncProvisioningLoop, "449");
            await ProvisionAsync(sentPolicyKey, cancellationToken).ConfigureAwait(false);
            return await PostAsync(command, bytes, contentType, cancellationToken, false, allowRedirect, http).ConfigureAwait(false);
        }

        EnsureHttpSuccess(response);
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new EasResponseTooLargeException();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > MaximumResponseBytes)
                throw new EasResponseTooLargeException();

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
            return null;

        var result = EasWbxml.Decode(buffer.ToArray());
        if (command == "Provision")
            return result;

        var status = StatusOf(result);
        if (status == "140")
            await HandleRemoteWipeAsync(cancellationToken).ConfigureAwait(false);

        if (status is "142" or "143" or "144")
        {
            if (!allowProvision)
                throw new EasPolicyException(Translator.ExchangeSetup_ActiveSyncProvisioningLoop, status);
            await ProvisionAsync(sentPolicyKey, cancellationToken).ConfigureAwait(false);
            return await PostAsync(command, bytes, contentType, cancellationToken, false, allowRedirect, http).ConfigureAwait(false);
        }

        ThrowOnAccountStatus(status);
        return result;
    }

    /// <summary>
    /// Provisions once for every request that the server refused with <paramref name="refusedPolicyKey"/>: requests
    /// that were refused while another one provisioned find a newer key after the wait and are retried with it.
    /// </summary>
    private async Task ProvisionAsync(string refusedPolicyKey, CancellationToken cancellationToken)
    {
        await _provisionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await EnsureStateAsync().ConfigureAwait(false);
            if (state.PolicyKey != refusedPolicyKey)
                return;
            await ProvisionCoreAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally { _provisionGate.Release(); }
    }

    private async Task ProvisionCoreAsync(EasAccountState state, CancellationToken cancellationToken)
    {
        XNamespace provision = "Provision:";
        var response = await PostProvisionAsync(CreatePolicyRequest(), "0", cancellationToken).ConfigureAwait(false);
        var policy = response.Element(provision + "Policies")?.Element(provision + "Policy")
            ?? throw new InvalidDataException("The EAS provisioning policy is missing.");

        // Policy status 2: the server has no policy for this client, so there is nothing to acknowledge.
        if (StatusOf(policy) == "2")
        {
            await SavePolicyAsync(state, policy.Element(provision + "PolicyKey")?.Value ?? "0", EasPolicyLimits.None).ConfigureAwait(false);
            return;
        }
        RequireSuccess(policy);

        var document = policy.Element(provision + "Data")?.Element(provision + "EASProvisionDoc")
            ?? throw new InvalidDataException("The EAS security policy is missing.");
        var evaluation = EasPolicy.Parse(document);
        var unenforceable = evaluation.Unenforceable;
        if (unenforceable.Count > 0)
            Logger.Warning("The EAS policy for account {AccountId} asks for settings Wino cannot enforce: {Settings}. Acknowledging it as not applied.",
                _account.Id, string.Join(", ", unenforceable));

        var temporaryKey = policy.Element(provision + "PolicyKey")?.Value ?? throw new InvalidDataException("The EAS policy key is missing.");
        var acknowledgement = new XElement(provision + "Provision", new XElement(provision + "Policies", new XElement(provision + "Policy",
            new XElement(provision + "PolicyType", PolicyType), new XElement(provision + "PolicyKey", temporaryKey),
            new XElement(provision + "Status", evaluation.AcknowledgementStatus))));

        XElement accepted;
        try { accepted = await PostProvisionAsync(acknowledgement, temporaryKey, cancellationToken).ConfigureAwait(false); }
        catch (EasPolicyException exception) when (exception.Status == "139" && unenforceable.Count > 0)
        {
            throw new EasPolicyException(string.Format(Translator.ExchangeSetup_ActiveSyncPolicyNotSupported, string.Join(", ", unenforceable)), "139");
        }

        var acceptedPolicy = accepted.Element(provision + "Policies")?.Element(provision + "Policy")
            ?? throw new InvalidDataException("The EAS accepted policy is missing.");
        RequireSuccess(acceptedPolicy);
        var permanentKey = acceptedPolicy.Element(provision + "PolicyKey")?.Value;

        // Exchange answers a "not applied" acknowledgment with success but without a permanent key, and refuses every
        // later request with 144, so the device cannot synchronize under this policy.
        if (permanentKey == null && unenforceable.Count > 0)
            throw new EasPolicyException(string.Format(Translator.ExchangeSetup_ActiveSyncPolicyNotSupported, string.Join(", ", unenforceable)), "3");
        await SavePolicyAsync(state, permanentKey ?? throw new InvalidDataException("The EAS accepted policy key is missing."), evaluation.Limits).ConfigureAwait(false);
    }

    private XElement CreatePolicyRequest()
    {
        XNamespace provision = "Provision:";
        XNamespace settings = "Settings:";
        var request = new XElement(provision + "Provision",
            new XElement(provision + "Policies", new XElement(provision + "Policy", new XElement(provision + "PolicyType", PolicyType))));
        if (IsAtLeast("14.1"))
            request.AddFirst(new XElement(settings + "DeviceInformation", DeviceInformationSet()));
        return request;
    }

    private async Task<XElement> PostProvisionAsync(XElement request, string policyKey, CancellationToken cancellationToken)
    {
        var response = await PostAsync("Provision", EasWbxml.Encode(request), "application/vnd.ms-sync.wbxml", cancellationToken, false, policyKey: policyKey).ConfigureAwait(false)
            ?? throw new InvalidDataException("The EAS provisioning response is empty.");
        if (WipeDirectiveOf(response) is { } directive)
            await RemoteWipeAsync(directive, cancellationToken).ConfigureAwait(false);

        ThrowOnAccountStatus(StatusOf(response));
        RequireSuccess(response);
        return response;
    }

    /// <summary>
    /// Status 140: the client asks for the wipe directive with an empty Provision element and policy key 0
    /// (MS-ASPROV 3.1.5.1.1) and carries it out.
    /// </summary>
    private async Task HandleRemoteWipeAsync(CancellationToken cancellationToken)
    {
        XNamespace provision = "Provision:";
        await _provisionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await PostAsync("Provision", EasWbxml.Encode(new XElement(provision + "Provision")), "application/vnd.ms-sync.wbxml",
                cancellationToken, false, policyKey: "0").ConfigureAwait(false)
                ?? throw new InvalidDataException("The EAS provisioning response is empty.");
            var directive = WipeDirectiveOf(response)
                ?? throw new InvalidDataException("The EAS server reported a remote wipe (status 140) but its Provision response carries no wipe directive.");
            await RemoteWipeAsync(directive, cancellationToken).ConfigureAwait(false);
        }
        finally { _provisionGate.Release(); }
    }

    private static string? WipeDirectiveOf(XElement response)
    {
        XNamespace provision = "Provision:";
        return response.Element(provision + "AccountOnlyRemoteWipe") != null ? "AccountOnlyRemoteWipe"
            : response.Element(provision + "RemoteWipe") != null ? "RemoteWipe"
            : null;
    }

    /// <summary>
    /// Wino cannot reset the computer, so for either directive it destroys what it received from the server and the
    /// stored credentials, which MS-ASPROV 3.1.5.1.2.2 requires of a client that does not wipe the device, and reports
    /// that result. A connection check has no account to erase, so it leaves the directive to the synchronization.
    /// </summary>
    private async Task RemoteWipeAsync(string directive, CancellationToken cancellationToken)
    {
        XNamespace provision = "Provision:";
        if (_accountData == null)
        {
            Logger.Warning("The EAS server requested {WipeKind} during a connection check for account {AccountId}; it is carried out by the next synchronization.",
                directive, _account.Id);
            throw new EasRemoteWipeException(Translator.ExchangeSetup_ActiveSyncRemoteWipePending);
        }

        Logger.Warning("The EAS server requested {WipeKind} for account {AccountId}. Erasing the account's data and credentials.", directive, _account.Id);
        var result = "1";
        try
        {
            await _accountData.EraseForRemoteWipeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.Error(exception, "Erasing account {AccountId} for an EAS {WipeKind} failed.", _account.Id, directive);
            result = "2";
        }

        // The client does not rely on the server's answer to the acknowledgment (MS-ASPROV 3.1.5.1.2.2); it is logged only.
        try
        {
            var answer = await PostAsync("Provision", EasWbxml.Encode(new XElement(provision + "Provision",
                new XElement(provision + directive, new XElement(provision + "Status", result)))), "application/vnd.ms-sync.wbxml",
                cancellationToken, false, policyKey: "0").ConfigureAwait(false);
            Logger.Information("The EAS server answered the {WipeKind} acknowledgment (status {Result}) for account {AccountId} with status {Status}.",
                directive, result, _account.Id, answer == null ? "none" : StatusOf(answer) ?? "none");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.Warning(exception, "Sending the EAS {WipeKind} acknowledgment for account {AccountId} failed.", directive, _account.Id);
        }
        finally
        {
            await MarkRemoteWipeAsync().ConfigureAwait(false);
        }

        throw new EasRemoteWipeException(Translator.ExchangeSetup_ActiveSyncRemoteWipe);
    }

    private async Task MarkRemoteWipeAsync()
    {
        var state = _state ?? await EnsureStateAsync().ConfigureAwait(false);
        state.IsRemoteWipeRequested = true;
        state.PolicyKey = "0";
        await SaveStateAsync().ConfigureAwait(false);
    }

    private async Task SavePolicyAsync(EasAccountState state, string policyKey, EasPolicyLimits limits)
    {
        state.PolicyKey = policyKey;
        state.IsPolicyRecorded = true;
        state.PolicyMaxEmailAgeFilter = limits.MaxEmailAgeFilter;
        state.PolicyMaxCalendarAgeFilter = limits.MaxCalendarAgeFilter;
        state.PolicyMaxAttachmentSize = limits.MaxAttachmentSize;
        state.PolicyMaxEmailBodyTruncationSize = limits.MaxEmailBodyTruncationSize;
        state.PolicyMaxEmailHtmlBodyTruncationSize = limits.MaxEmailHtmlBodyTruncationSize;
        state.PolicyAttachmentsDisabled = limits.AttachmentsDisabled;
        await SaveStateAsync().ConfigureAwait(false);
    }

    private static void ThrowOnAccountStatus(string? status)
    {
        switch (status)
        {
            case "139" or "141" or "145":
                throw new EasPolicyException(Translator.ExchangeSetup_ActiveSyncPolicyRejected, status);
            case "126" or "127" or "128" or "129" or "130" or "131" or "177":
                throw new EasAccessDeniedException(Translator.ExchangeSetup_ActiveSyncAccessDenied, status);
            case "111":
                throw new EasServerBusyException(Translator.ExchangeSetup_ActiveSyncServerBusy, null);
        }
    }

    private async Task<EasAccountState> EnsureStateAsync()
    {
        if (_state != null)
            return _state.IsRemoteWipeRequested ? throw new EasRemoteWipeException(Translator.ExchangeSetup_ActiveSyncRemoteWipe) : _state;

        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state != null)
                return _state;

            var state = _store == null
                ? new EasAccountState { AccountId = _account.Id, DeviceId = _deviceIdOverride ?? _account.Id.ToString("N") }
                : await _store.LoadAsync(_account.Id).ConfigureAwait(false);
            if (state.IsRemoteWipeRequested)
            {
                // A wipe acknowledged before the client erased data keeps the partnership flagged; the data goes now.
                if (_accountData != null)
                    await _accountData.EraseForRemoteWipeAsync().ConfigureAwait(false);
                throw new EasRemoteWipeException(Translator.ExchangeSetup_ActiveSyncRemoteWipe);
            }

            if (state.ConfiguredEndpoint == _configuredEndpoint.AbsoluteUri && NormalizeEndpoint(state.ResolvedEndpoint) is { } resolved)
                _endpoint = resolved;
            else if (state.ConfiguredEndpoint != _configuredEndpoint.AbsoluteUri)
            {
                state.ConfiguredEndpoint = _configuredEndpoint.AbsoluteUri;
                state.ResolvedEndpoint = string.Empty;
            }

            _state = state;
            return state;
        }
        finally { _stateGate.Release(); }
    }

    private Task SaveStateAsync()
        => _store != null && _state != null ? _store.SaveAsync(_state) : Task.CompletedTask;

    /// <summary>HTTP 451: the mailbox lives behind another endpoint (X-MS-Location). Followed once and remembered.</summary>
    private async Task<bool> TryFollowMailboxRedirectAsync(HttpResponseMessage response)
    {
        if ((int)response.StatusCode != 451)
            return false;

        var location = response.Headers.TryGetValues("X-MS-Location", out var values) ? values.FirstOrDefault() : null;
        var target = location == null ? null : NormalizeEndpoint(location);
        if (target == null || target == _endpoint)
            throw new HttpRequestException("The EAS server moved this mailbox but did not name a usable HTTPS endpoint.", null, response.StatusCode);

        Logger.Information("EAS mailbox for account {AccountId} moved to {Host}.", _account.Id, target.Host);
        _endpoint = target;
        var state = await EnsureStateAsync().ConfigureAwait(false);
        state.ConfiguredEndpoint = _configuredEndpoint.AbsoluteUri;
        state.ResolvedEndpoint = target.AbsoluteUri;
        await SaveStateAsync().ConfigureAwait(false);
        return true;
    }

    private HttpRequestMessage CreateRequest(string command, byte[] bytes, string contentType, string deviceId, string policyKey)
    {
        var uri = new Uri(_endpoint.AbsoluteUri + "?Cmd=" + command + "&User=" + Uri.EscapeDataString(_user) +
            "&DeviceId=" + deviceId + "&DeviceType=WinoMail");
        var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("MS-ASProtocolVersion", _version);
        request.Headers.Add("X-MS-PolicyKey", policyKey);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return request;
    }

    internal async Task<XElement> FolderSyncAsync(string key, CancellationToken cancellationToken)
    {
        XNamespace folder = "FolderHierarchy:";
        var result = await CommandAsync("FolderSync", new XElement(folder + "FolderSync", new XElement(folder + "SyncKey", key)), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The EAS FolderSync response was empty.");
        RequireSuccess(result);
        return result;
    }

    internal async Task<XElement> SyncAsync(string folderId, string syncKey, string itemClass, XElement? commands, CancellationToken cancellationToken,
        int windowSize = 100, bool getChanges = true, bool deletesAsMoves = true)
    {
        if (_version == null)
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
        XNamespace sync = "AirSync:";
        var collection = new XElement(sync + "Collection",
            new XElement(sync + "SyncKey", syncKey),
            new XElement(sync + "CollectionId", folderId));
        if (_version == "12.0")
            collection.AddFirst(new XElement(sync + "Class", itemClass));
        if (syncKey == "0" && itemClass == "Contacts")
            collection.Add(new XElement(sync + "Supported"));
        if (syncKey != "0")
        {
            collection.Add(new XElement(sync + "DeletesAsMoves", deletesAsMoves ? "1" : "0"),
                new XElement(sync + "GetChanges", getChanges ? "1" : "0"),
                new XElement(sync + "WindowSize", windowSize.ToString(CultureInfo.InvariantCulture)),
                new XElement(sync + "Options", new XElement(sync + "Conflict", "0"), BodyPreference(itemClass)));
            var filterType = PolicyLimits.ClampFilterType(itemClass, 0);
            if (filterType != 0)
                collection.Element(sync + "Options")!.AddFirst(new XElement(sync + "FilterType", filterType.ToString(CultureInfo.InvariantCulture)));
        }

        if (commands != null)
            collection.Add(commands);

        var response = await CommandAsync("Sync", new XElement(sync + "Sync", new XElement(sync + "Collections", collection)), cancellationToken).ConfigureAwait(false);
        if (response == null && commands == null && syncKey != "0")
            return new XElement(sync + "Collection", new XElement(sync + "Status", "1"), new XElement(sync + "SyncKey", syncKey));
        if (response == null)
            throw new InvalidDataException("The EAS Sync response was empty.");
        if (response.Element(sync + "Status") != null)
            RequireSuccess(response);
        var result = response.Element(sync + "Collections")?.Element(sync + "Collection")
            ?? throw new InvalidDataException("The EAS Sync response has no collection.");
        RequireSuccess(result);
        return result;
    }

    /// <summary>
    /// Mail is synchronized without its MIME: 14.0 and later return only a 255-character preview, 12.x a plain-text
    /// body cut to the same length. The full message is fetched with ItemOperations when it opens.
    /// </summary>
    private XElement BodyPreference(string itemClass)
    {
        XNamespace body = "AirSyncBase:";
        if (itemClass != "Email")
            return new XElement(body + "BodyPreference", new XElement(body + "Type", "1"));
        return IsAtLeast("14.0")
            ? new XElement(body + "BodyPreference", new XElement(body + "Type", "2"), new XElement(body + "TruncationSize", "0"), new XElement(body + "Preview", "255"))
            : new XElement(body + "BodyPreference", new XElement(body + "Type", "1"),
                new XElement(body + "TruncationSize", Math.Min(255, PolicyLimits.MaxEmailBodyTruncationSize ?? 255).ToString(CultureInfo.InvariantCulture)));
    }

    internal async Task<XElement> FetchAsync(string folderId, string serverId, CancellationToken cancellationToken)
    {
        // The request carries the policy's truncation, which is known once connected.
        if (_version == null)
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
        XNamespace items = "ItemOperations:";
        XNamespace sync = "AirSync:";
        XNamespace body = "AirSyncBase:";
        var response = await CommandAsync("ItemOperations", new XElement(items + "ItemOperations",
            new XElement(items + "Fetch", new XElement(items + "Store", "Mailbox"),
                new XElement(sync + "CollectionId", folderId), new XElement(sync + "ServerId", serverId),
                new XElement(items + "Options", MimeBodyPreference(),
                    new XElement(sync + "MIMESupport", "2")))), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The EAS item fetch response was empty.");
        RequireSuccess(response);
        var fetch = response.Element(items + "Response")?.Element(items + "Fetch")
            ?? throw new InvalidDataException("The EAS item fetch response has no item.");
        RequireSuccess(fetch);
        return fetch.Element(items + "Properties") ?? throw new InvalidDataException("The EAS item has no properties.");
    }

    /// <summary>
    /// The whole message as MIME. A policy that truncates email bodies truncates it to the strictest of its plain-text
    /// and HTML limits, because the MIME carries whichever body the message has.
    /// </summary>
    private XElement MimeBodyPreference()
    {
        XNamespace body = "AirSyncBase:";
        var preference = new XElement(body + "BodyPreference", new XElement(body + "Type", "4"));
        if (PolicyLimits.BodyTruncation is { } truncation)
            preference.Add(new XElement(body + "TruncationSize", truncation.ToString(CultureInfo.InvariantCulture)));
        return preference;
    }

    /// <summary>
    /// Removes the attachments the device policy does not allow on this device: every attachment when attachments are
    /// disabled, otherwise those larger than MaxAttachmentSize. Every part that is not a text body counts as an
    /// attachment, as ActiveSync lists inline images among a message's attachments.
    /// </summary>
    internal void ApplyAttachmentPolicy(MimeMessage message)
    {
        var limits = PolicyLimits;
        if (!limits.AttachmentsDisabled && limits.MaxAttachmentSize == null)
            return;

        if (message.Body is { } body && RemoveDisallowedParts(body, limits) is var removed && removed > 0)
            Logger.Information("Removed {Count} attachment(s) the EAS device policy of account {AccountId} does not allow.", removed, _account.Id);
    }

    private static int RemoveDisallowedParts(MimeEntity entity, EasPolicyLimits limits)
    {
        if (entity is not Multipart multipart)
            return 0;

        var removed = 0;
        for (var index = multipart.Count - 1; index >= 0; index--)
        {
            var child = multipart[index];
            if (child is Multipart)
                removed += RemoveDisallowedParts(child, limits);
            else if (IsAttachment(child) && (limits.AttachmentsDisabled || SizeOf(child) > limits.MaxAttachmentSize))
            {
                multipart.RemoveAt(index);
                removed++;
            }
        }
        return removed;
    }

    private static bool IsAttachment(MimeEntity entity)
        => entity is MessagePart || entity is MimePart part && (part.IsAttachment || part is not TextPart);

    private static long SizeOf(MimeEntity entity)
    {
        using var measure = new MeasuringStream();
        if (entity is MimePart { Content: { } content })
            content.DecodeTo(measure);
        else if (entity is MessagePart { Message: { } message })
            message.WriteTo(measure);
        return measure.Length;
    }

    internal static void RequireSuccess(XElement response)
    {
        var status = StatusOf(response);
        if (status != "1")
            throw new EasCommandException(response.Name.LocalName, status ?? "missing");
    }

    private static string? StatusOf(XElement element)
        => element.Elements().FirstOrDefault(child => child.Name.LocalName == "Status")?.Value;

    private void EnsureHttpSuccess(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new AuthenticationAttentionException(_account, Translator.ExchangeSetup_ActiveSyncCredentialsRejected,
                new HttpRequestException("EAS returned HTTP 401.", null, response.StatusCode));

        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new EasAccessDeniedException(Translator.ExchangeSetup_ActiveSyncAccessDenied, "403");

        if (code is 503 or 429 || (!response.IsSuccessStatusCode && response.Headers.Contains("X-MS-ASThrottle")))
            throw new EasServerBusyException(Translator.ExchangeSetup_ActiveSyncServerBusy, RetryAfter(response));

        if (code is >= 300 and < 400)
            throw new HttpRequestException("The EAS server redirected the request. Configure its final HTTPS endpoint; credentials were not forwarded.", null, response.StatusCode);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("EAS returned HTTP " + code + ".", null, response.StatusCode);
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return delta;
        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : null;
        }
        return response.Headers.TryGetValues("Retry-After", out var values) &&
            int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    private static Uri? NormalizeEndpoint(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return null;

        return uri.AbsolutePath is "/" or "" ? new Uri(uri, "/Microsoft-Server-ActiveSync") : uri;
    }

    public void Dispose()
    {
        _http.Dispose();
        if (_longPoll.IsValueCreated)
            _longPoll.Value.Dispose();
        _stateGate.Dispose();
        _provisionGate.Dispose();
    }
}
