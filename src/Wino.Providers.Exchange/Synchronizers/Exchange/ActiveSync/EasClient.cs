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
    private readonly MailAccount _account;
    private readonly Uri _configuredEndpoint;
    private readonly string _user;
    private readonly EasAccountStateStore? _store;
    private readonly string? _deviceIdOverride;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private EasAccountState? _state;
    private Uri _endpoint;
    private string? _version;

    /// <param name="store">Persists the device partnership; null keeps it in memory, as a connection probe does.</param>
    /// <param name="deviceId">A device id for a probe that must not touch the live partnership's sync state.</param>
    internal EasClient(MailAccount account, string endpoint, string user, string password, EasAccountStateStore? store,
        string? deviceId = null, HttpMessageHandler? handler = null)
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
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinoMail/2.0");
    }

    /// <summary>The negotiated MS-ASProtocolVersion, or null before the first request.</summary>
    internal string? ProtocolVersion => _version;

    internal string? DeviceId => _state?.DeviceId;

    internal string? PolicyKey => _state?.PolicyKey;

    internal Uri Endpoint => _endpoint;

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

        if (state.ProtocolVersion != _version)
        {
            if (!string.IsNullOrEmpty(state.ProtocolVersion))
                Logger.Information("EAS protocol version for account {AccountId} changed from {Previous} to {Version}.", _account.Id, state.ProtocolVersion, _version);
            state.ProtocolVersion = _version;
            await SaveStateAsync().ConfigureAwait(false);
        }
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

    private async Task<XElement?> PostAsync(string command, byte[] bytes, string contentType, CancellationToken cancellationToken,
        bool allowProvision = true, bool allowRedirect = true)
    {
        var state = await EnsureStateAsync().ConfigureAwait(false);
        using var request = CreateRequest(command, bytes, contentType, state);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (allowRedirect && await TryFollowMailboxRedirectAsync(response).ConfigureAwait(false))
            return await PostAsync(command, bytes, contentType, cancellationToken, allowProvision, false).ConfigureAwait(false);

        if ((int)response.StatusCode == 449)
        {
            if (!allowProvision)
                throw new EasPolicyException(Translator.ExchangeSetup_ActiveSyncProvisioningLoop, "449");
            await ProvisionAsync(cancellationToken).ConfigureAwait(false);
            return await PostAsync(command, bytes, contentType, cancellationToken, false, allowRedirect).ConfigureAwait(false);
        }

        EnsureHttpSuccess(response);
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidDataException("The EAS response exceeds the size limit.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("The EAS response exceeds the size limit.");

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
            await ProvisionAsync(cancellationToken).ConfigureAwait(false);
            return await PostAsync(command, bytes, contentType, cancellationToken, false, allowRedirect).ConfigureAwait(false);
        }

        ThrowOnAccountStatus(status);
        return result;
    }

    private async Task ProvisionAsync(CancellationToken cancellationToken)
    {
        var state = await EnsureStateAsync().ConfigureAwait(false);
        XNamespace provision = "Provision:";
        state.PolicyKey = "0";
        var response = await PostProvisionAsync(CreatePolicyRequest(), cancellationToken).ConfigureAwait(false);
        var policy = response.Element(provision + "Policies")?.Element(provision + "Policy")
            ?? throw new InvalidDataException("The EAS provisioning policy is missing.");

        // Policy status 2: the server has no policy for this client, so there is nothing to acknowledge.
        if (StatusOf(policy) == "2")
        {
            await SavePolicyKeyAsync(state, policy.Element(provision + "PolicyKey")?.Value ?? "0").ConfigureAwait(false);
            return;
        }
        RequireSuccess(policy);

        var document = policy.Element(provision + "Data")?.Element(provision + "EASProvisionDoc")
            ?? throw new InvalidDataException("The EAS security policy is missing.");
        var evaluation = EasPolicy.Parse(document);
        var unenforceable = evaluation.Unenforceable;
        if (unenforceable.Count > 0)
            Logger.Warning("The EAS policy for account {AccountId} asks for settings Wino cannot enforce: {Settings}. Acknowledging it as partially applied.",
                _account.Id, string.Join(", ", unenforceable));

        var temporaryKey = policy.Element(provision + "PolicyKey")?.Value ?? throw new InvalidDataException("The EAS policy key is missing.");
        var acknowledgement = new XElement(provision + "Provision", new XElement(provision + "Policies", new XElement(provision + "Policy",
            new XElement(provision + "PolicyType", PolicyType), new XElement(provision + "PolicyKey", temporaryKey),
            new XElement(provision + "Status", evaluation.AcknowledgementStatus))));

        XElement accepted;
        try { accepted = await PostProvisionAsync(acknowledgement, cancellationToken).ConfigureAwait(false); }
        catch (EasPolicyException exception) when (exception.Status == "139" && unenforceable.Count > 0)
        {
            throw new EasPolicyException(string.Format(Translator.ExchangeSetup_ActiveSyncPolicyNotSupported, string.Join(", ", unenforceable)), "139");
        }

        var acceptedPolicy = accepted.Element(provision + "Policies")?.Element(provision + "Policy")
            ?? throw new InvalidDataException("The EAS accepted policy is missing.");
        RequireSuccess(acceptedPolicy);
        await SavePolicyKeyAsync(state, acceptedPolicy.Element(provision + "PolicyKey")?.Value
            ?? throw new InvalidDataException("The EAS accepted policy key is missing.")).ConfigureAwait(false);
    }

    private XElement CreatePolicyRequest()
    {
        XNamespace provision = "Provision:";
        XNamespace settings = "Settings:";
        var request = new XElement(provision + "Provision",
            new XElement(provision + "Policies", new XElement(provision + "Policy", new XElement(provision + "PolicyType", PolicyType))));
        if (IsAtLeast("14.1"))
            request.AddFirst(new XElement(settings + "DeviceInformation", new XElement(settings + "Set",
                new XElement(settings + "Model", "Wino Mail"),
                new XElement(settings + "OS", Environment.OSVersion.VersionString),
                new XElement(settings + "UserAgent", "WinoMail/2.0"))));
        return request;
    }

    private async Task<XElement> PostProvisionAsync(XElement request, CancellationToken cancellationToken)
    {
        XNamespace provision = "Provision:";
        var response = await PostAsync("Provision", EasWbxml.Encode(request), "application/vnd.ms-sync.wbxml", cancellationToken, false).ConfigureAwait(false)
            ?? throw new InvalidDataException("The EAS provisioning response is empty.");
        if (response.Element(provision + "RemoteWipe") != null || response.Element(provision + "AccountOnlyRemoteWipe") != null)
            await AcknowledgeRemoteWipeAsync(response, cancellationToken).ConfigureAwait(false);

        ThrowOnAccountStatus(StatusOf(response));
        RequireSuccess(response);
        return response;
    }

    /// <summary>
    /// Status 140. Exchange sends RemoteWipe next to the policy in the Provision response; an account-only wipe was
    /// never delivered to this client in the lab, so the status alone also stops the account.
    /// </summary>
    private async Task HandleRemoteWipeAsync(CancellationToken cancellationToken)
    {
        XNamespace provision = "Provision:";
        var state = await EnsureStateAsync().ConfigureAwait(false);
        state.PolicyKey = "0";
        var response = await PostAsync("Provision", EasWbxml.Encode(CreatePolicyRequest()), "application/vnd.ms-sync.wbxml", cancellationToken, false).ConfigureAwait(false);
        if (response != null && (response.Element(provision + "RemoteWipe") != null || response.Element(provision + "AccountOnlyRemoteWipe") != null))
            await AcknowledgeRemoteWipeAsync(response, cancellationToken).ConfigureAwait(false);

        await MarkRemoteWipeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Wino never erases mail on a server's request. It answers 2 (wipe failed), the truthful MS-ASPROV result, so the
    /// administrator does not see a wipe reported as done, then stops using the partnership.
    /// </summary>
    private async Task AcknowledgeRemoteWipeAsync(XElement response, CancellationToken cancellationToken)
    {
        XNamespace provision = "Provision:";
        var element = response.Element(provision + "AccountOnlyRemoteWipe") != null ? "AccountOnlyRemoteWipe" : "RemoteWipe";
        Logger.Warning("The EAS server requested {WipeKind} for account {AccountId}. Synchronization stops; no local data is erased.", element, _account.Id);
        try
        {
            await PostAsync("Provision", EasWbxml.Encode(new XElement(provision + "Provision",
                new XElement(provision + element, new XElement(provision + "Status", "2")))), "application/vnd.ms-sync.wbxml", cancellationToken, false).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.Warning(exception, "Acknowledging the EAS remote wipe for account {AccountId} failed.", _account.Id);
        }

        await MarkRemoteWipeAsync().ConfigureAwait(false);
    }

    private async Task MarkRemoteWipeAsync()
    {
        var state = await EnsureStateAsync().ConfigureAwait(false);
        state.IsRemoteWipeRequested = true;
        state.PolicyKey = "0";
        await SaveStateAsync().ConfigureAwait(false);
        throw new EasRemoteWipeException(Translator.ExchangeSetup_ActiveSyncRemoteWipe);
    }

    private async Task SavePolicyKeyAsync(EasAccountState state, string policyKey)
    {
        state.PolicyKey = policyKey;
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
                throw new EasRemoteWipeException(Translator.ExchangeSetup_ActiveSyncRemoteWipe);

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

    private HttpRequestMessage CreateRequest(string command, byte[] bytes, string contentType, EasAccountState state)
    {
        var uri = new Uri(_endpoint.AbsoluteUri + "?Cmd=" + command + "&User=" + Uri.EscapeDataString(_user) +
            "&DeviceId=" + state.DeviceId + "&DeviceType=WinoMail");
        var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("MS-ASProtocolVersion", _version);
        request.Headers.Add("X-MS-PolicyKey", state.PolicyKey);
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

    internal async Task<XElement> SyncAsync(string folderId, string syncKey, string itemClass, XElement? commands, CancellationToken cancellationToken)
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
            collection.Add(new XElement(sync + "DeletesAsMoves", "1"),
                new XElement(sync + "GetChanges", "1"),
                new XElement(sync + "WindowSize", "100"),
                new XElement(sync + "Options", new XElement(sync + "Conflict", "0"), BodyPreference(itemClass)));
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
        foreach (var operation in result.Element(sync + "Responses")?.Elements() ?? Enumerable.Empty<XElement>())
            RequireSuccess(operation);

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
            : new XElement(body + "BodyPreference", new XElement(body + "Type", "1"), new XElement(body + "TruncationSize", "255"));
    }

    internal async Task<XElement> FetchAsync(string folderId, string serverId, CancellationToken cancellationToken)
    {
        XNamespace items = "ItemOperations:";
        XNamespace sync = "AirSync:";
        XNamespace body = "AirSyncBase:";
        var response = await CommandAsync("ItemOperations", new XElement(items + "ItemOperations",
            new XElement(items + "Fetch", new XElement(items + "Store", "Mailbox"),
                new XElement(sync + "CollectionId", folderId), new XElement(sync + "ServerId", serverId),
                new XElement(items + "Options", new XElement(body + "BodyPreference", new XElement(body + "Type", "4")),
                    new XElement(sync + "MIMESupport", "2")))), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The EAS item fetch response was empty.");
        RequireSuccess(response);
        var fetch = response.Element(items + "Response")?.Element(items + "Fetch")
            ?? throw new InvalidDataException("The EAS item fetch response has no item.");
        RequireSuccess(fetch);
        return fetch.Element(items + "Properties") ?? throw new InvalidDataException("The EAS item has no properties.");
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
        _stateGate.Dispose();
    }
}
