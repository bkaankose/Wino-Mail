#nullable enable
using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Services.PushRelay;

/// <summary>
/// Gets relay tokens from the Wino Mail API and caches them until <see cref="PushRelayDefaults.TokenRenewalMargin"/>
/// before they expire. Concurrent callers share one API call. Tokens, credentials and attestations are never logged.
/// </summary>
/// <remarks>
/// Device first: the install registers itself as an attested device and uses its credential. On mobile the signed-in
/// Wino Account is used once the device is linked to it (or when the device path is unavailable on a platform that must
/// attest). Desktop always uses the device credential, proven by its bound key. See <see cref="IPushRelayTokenProvider"/>.
/// </remarks>
public sealed class PushRelayTokenProvider : IPushRelayTokenProvider
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DevicePathRetryDelay = TimeSpan.FromMinutes(10);

    private readonly IWinoAccountApiClient _apiClient;
    private readonly IWinoAccountSessionService? _sessions;
    private readonly IPushRelayDeviceCredentialStore? _credentials;
    private readonly IDeviceAttestationProvider? _attestation;
    private readonly IPushRelayDeviceInfo? _deviceInfo;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger = Log.ForContext<PushRelayTokenProvider>();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _deviceLock = new(1, 1);
    private PushRelayAccessToken? _cached;
    private Task<PushRelayAccessToken>? _pending;
    private int _generation;
    private int? _mailboxLimit;
    private PushRelayDeviceCredential? _credential;
    private bool _credentialLoaded;
    private DateTimeOffset? _devicePathUnavailableUntil;
    private bool _boundKeyUpgradeAttempted;

    public PushRelayTokenProvider(IWinoAccountApiClient apiClient, TimeProvider? timeProvider = null)
        : this(apiClient, null, null, null, null, timeProvider)
    {
    }

    public PushRelayTokenProvider(IWinoAccountApiClient apiClient, IWinoAccountSessionService? sessions,
        IPushRelayDeviceCredentialStore? credentials, IDeviceAttestationProvider? attestation, IPushRelayDeviceInfo? deviceInfo,
        TimeProvider? timeProvider = null)
    {
        _apiClient = apiClient;
        _sessions = sessions;
        _credentials = credentials;
        _attestation = attestation;
        _deviceInfo = deviceInfo;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int? MailboxLimit { get { lock (_gate) return _mailboxLimit; } }

    public Task<PushRelayAccessToken> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        Task<PushRelayAccessToken> pending;
        lock (_gate)
        {
            if (_cached is { } cached && _timeProvider.GetUtcNow() < cached.ExpiresUtc - PushRelayDefaults.TokenRenewalMargin)
                return Task.FromResult(cached);

            // One shared call for every waiter; a single caller's cancellation must not fail the others.
            pending = _pending ??= FetchAsync(_generation);
        }

        return pending.WaitAsync(cancellationToken);
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
            _pending = null;
            _generation++;
        }
    }

    public async Task<bool> RequiresSignInAsync(CancellationToken cancellationToken = default)
    {
        if (await GetDeviceCredentialAsync(cancellationToken).ConfigureAwait(false) is not null)
            return false;

        // Desktop never needs a Wino Account: a missing credential (API device cap, unreachable API) is retried with backoff.
        if (IsDesktop)
            return false;

        return await CaptureSessionAsync(cancellationToken).ConfigureAwait(false) is null;
    }

    /// <summary>Windows and macOS register with a bound key (or without attestation) and must work without a Wino Account.</summary>
    private bool IsDesktop => _deviceInfo?.Describe().Platform is PushRelayPlatforms.Windows or PushRelayPlatforms.MacOS;

    public async Task<PushRelayDeviceCredential?> GetDeviceCredentialAsync(CancellationToken cancellationToken = default)
    {
        if (_credentials is null)
            return null;

        await _deviceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_credentialLoaded)
            {
                try { _credential = await _credentials.LoadAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "The relay device credential could not be read.");
                }
                _credentialLoaded = true;
            }

            if (_credential is { } existing && NeedsBoundKeyUpgrade(existing))
                await UpgradeToBoundKeyAsync(existing, cancellationToken).ConfigureAwait(false);

            if (_credential is not null)
                return _credential;

            if (_devicePathUnavailableUntil is { } until && _timeProvider.GetUtcNow() < until)
                return null;

            _credential = await RegisterDeviceAsync(cancellationToken).ConfigureAwait(false);
            return _credential;
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    public async Task<bool> LinkDeviceAsync(CancellationToken cancellationToken = default)
    {
        var credential = await GetDeviceCredentialAsync(cancellationToken).ConfigureAwait(false);
        var session = await CaptureSessionAsync(cancellationToken).ConfigureAwait(false);
        if (credential is null || session is null)
            return false;
        if (credential.LinkedWinoUserId == session.AccountId)
            return true;

        var assertion = await CreateAssertionAsync(credential, cancellationToken).ConfigureAwait(false);
        var envelope = await _apiClient.LinkRelayDeviceAsync(credential.DeviceId, credential.Secret, assertion, cancellationToken).ConfigureAwait(false);
        if (!envelope.IsSuccess)
        {
            if (IsDeviceGone(envelope.ErrorCode))
            {
                await ForgetDeviceAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            _logger.Warning("Linking the relay device to the Wino Account failed with error code {ErrorCode}.", envelope.ErrorCode);
            return false;
        }

        await StoreCredentialAsync(credential with { LinkedWinoUserId = session.AccountId }, cancellationToken).ConfigureAwait(false);
        Invalidate();
        _logger.Information("Relay device linked to the signed-in Wino Account.");
        return true;
    }

    public async Task UnlinkDeviceAsync(CancellationToken cancellationToken = default)
    {
        var credential = await GetDeviceCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (credential is null || credential.LinkedWinoUserId is null)
        {
            Invalidate();
            return;
        }

        try
        {
            var assertion = await CreateAssertionAsync(credential, cancellationToken).ConfigureAwait(false);
            var envelope = await _apiClient.UnlinkRelayDeviceAsync(credential.DeviceId, credential.Secret, assertion, cancellationToken).ConfigureAwait(false);
            if (!envelope.IsSuccess)
                _logger.Information("Unlinking the relay device answered {ErrorCode}; the API's account sign-out covers it.", envelope.ErrorCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Information(ex, "Unlinking the relay device failed; the API's account sign-out covers it.");
        }

        await StoreCredentialAsync(credential with { LinkedWinoUserId = null }, cancellationToken).ConfigureAwait(false);
        Invalidate();
    }

    public async Task ForgetDeviceAsync(CancellationToken cancellationToken = default)
    {
        await _deviceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _credential = null;
            _credentialLoaded = true;
            if (_credentials is not null)
            {
                try { await _credentials.DeleteAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "The relay device credential could not be deleted.");
                }
            }
        }
        finally
        {
            _deviceLock.Release();
        }

        Invalidate();
    }

    private async Task StoreCredentialAsync(PushRelayDeviceCredential credential, CancellationToken cancellationToken)
    {
        await _deviceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _credential = credential;
            _credentialLoaded = true;
            if (_credentials is not null)
                await _credentials.SaveAsync(credential, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    private bool NeedsBoundKeyUpgrade(PushRelayDeviceCredential credential)
        => !_boundKeyUpgradeAttempted && IsDesktop && credential.AttestationKind == DeviceAttestationKind.None
            && _attestation is { Kind: DeviceAttestationKind.BoundKey, IsSupported: true };

    /// <summary>
    /// Replaces a desktop credential registered before bound keys with a bound-key device, once per run. The new device
    /// registers first, so a failure (key store, API) keeps the old credential working. Runs under <see cref="_deviceLock"/>.
    /// </summary>
    private async Task UpgradeToBoundKeyAsync(PushRelayDeviceCredential existing, CancellationToken cancellationToken)
    {
        _boundKeyUpgradeAttempted = true;

        PushRelayDeviceCredential? replacement;
        try
        {
            replacement = await RegisterDeviceAsync(cancellationToken, upgrading: true).ConfigureAwait(false);
        }
        catch (PushRelayApiException ex)
        {
            _logger.Information("The relay device keeps its unbound credential; the bound-key registration failed ({Code}).", ex.Code);
            return;
        }

        if (replacement is null)
            return;

        try
        {
            var envelope = await _apiClient.RevokeRelayDeviceAsync(existing.DeviceId, existing.Secret, null, cancellationToken).ConfigureAwait(false);
            if (!envelope.IsSuccess)
                _logger.Information("Revoking the replaced relay device answered {ErrorCode}.", envelope.ErrorCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Information("Revoking the replaced relay device failed ({Error}).", ex.GetType().Name);
        }

        _credential = replacement;
        Invalidate();
        _logger.Information("Relay device {OldDeviceId} replaced by bound-key device {DeviceId}.", existing.DeviceId, replacement.DeviceId);
    }

    /// <summary>
    /// Registers this install with the API: challenge, platform attestation, registration. Runs under <see cref="_deviceLock"/>.
    /// While <paramref name="upgrading"/>, only a bound-key registration counts and nothing marks the device path unavailable.
    /// </summary>
    private async Task<PushRelayDeviceCredential?> RegisterDeviceAsync(CancellationToken cancellationToken, bool upgrading = false)
    {
        if (_deviceInfo is null)
            return null;

        var platform = _deviceInfo.Describe().Platform;
        var mustAttest = platform is not (PushRelayPlatforms.Windows or PushRelayPlatforms.MacOS);
        var attestation = _attestation;

        if (mustAttest && (attestation is null || attestation.Kind == DeviceAttestationKind.None || !attestation.IsSupported))
        {
            // Simulator, an Android build the Play Store does not know, or no provider: the user path is the fallback.
            MarkDevicePathUnavailable("attestation unavailable");
            return null;
        }

        try
        {
            var challenge = await _apiClient.CreateRelayDeviceChallengeAsync(platform, cancellationToken).ConfigureAwait(false);
            if (!challenge.IsSuccess || challenge.Result is not { } nonce || string.IsNullOrWhiteSpace(nonce.Nonce))
                throw MapFailure(challenge.IsSuccess ? WinoAccountClientErrorCodes.InvalidServiceResponse : challenge.ErrorCode, devicePath: true);

            DeviceAttestation statement;
            if (attestation is null || attestation.Kind == DeviceAttestationKind.None)
            {
                statement = DeviceAttestation.None;
            }
            else
            {
                try
                {
                    statement = await attestation.AttestAsync(nonce.Nonce, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && upgrading)
                {
                    _logger.Warning(ex, "The relay device key could not be created; the device keeps its unbound credential.");
                    return null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !mustAttest)
                {
                    // A desktop with a broken key store still gets live notifications, unbound.
                    _logger.Warning(ex, "The relay device key could not be created; registering without one.");
                    statement = DeviceAttestation.None;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Device attestation failed; relay tokens need a Wino Account until it works.");
                    MarkDevicePathUnavailable("attestation failed");
                    return null;
                }
            }

            if (upgrading && statement.Kind != DeviceAttestationKind.BoundKey)
                return null;

            // A bound key's KeyId names the local key; only the public key and the nonce signature go to the API.
            var request = new RegisterRelayDeviceRequest(platform, nonce.Nonce, statement.Kind == DeviceAttestationKind.BoundKey
                ? new RelayDeviceAttestationRequest(ToApiKind(statement.Kind), PublicKey: statement.PublicKey, Signature: statement.Signature)
                : new RelayDeviceAttestationRequest(ToApiKind(statement.Kind), statement.KeyId, statement.AttestationObject, statement.Token));
            var registration = await _apiClient.RegisterRelayDeviceAsync(request, cancellationToken).ConfigureAwait(false);
            if (!registration.IsSuccess || registration.Result is not { } device || device.DeviceId == Guid.Empty || string.IsNullOrWhiteSpace(device.Credential))
            {
                if (upgrading)
                {
                    _logger.Information("The API refused the bound-key registration ({ErrorCode}); the device keeps its unbound credential.", registration.ErrorCode);
                    return null;
                }

                if (registration.ErrorCode is RelayDeviceErrorCodes.AttestationRequired or RelayDeviceErrorCodes.AttestationInvalid
                    or RelayDeviceErrorCodes.AttestationUnavailable or RelayDeviceErrorCodes.DeviceCreationLimit)
                {
                    _logger.Warning("The API refused the device attestation ({ErrorCode}); relay tokens need a Wino Account until it works.", registration.ErrorCode);
                    MarkDevicePathUnavailable(registration.ErrorCode);
                    return null;
                }

                throw MapFailure(registration.IsSuccess ? WinoAccountClientErrorCodes.InvalidServiceResponse : registration.ErrorCode, devicePath: true);
            }

            var credential = new PushRelayDeviceCredential(device.DeviceId, device.Credential, platform, statement.Kind, statement.KeyId,
                LinkedWinoUserId: null, _timeProvider.GetUtcNow().UtcDateTime);
            if (_credentials is not null)
                await _credentials.SaveAsync(credential, cancellationToken).ConfigureAwait(false);

            _devicePathUnavailableUntil = null;
            _logger.Information("Registered this install as a relay device ({Attestation}).", statement.Kind);
            return credential;
        }
        catch (PushRelayApiException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Relay device registration failed unexpectedly.");
            throw new PushRelayApiException(new PushRelayProblem(PushRelayErrorCodes.ServiceUnavailable, 0, Detail: ex.GetType().Name), ex);
        }
    }

    private void MarkDevicePathUnavailable(string? reason)
    {
        _devicePathUnavailableUntil = _timeProvider.GetUtcNow() + DevicePathRetryDelay;
        _logger.Information("Relay device path unavailable ({Reason}); next attempt after {Delay}.", reason, DevicePathRetryDelay);
    }

    private static string ToApiKind(DeviceAttestationKind kind) => kind switch
    {
        DeviceAttestationKind.AppAttest => RelayDeviceAttestationKinds.AppAttest,
        DeviceAttestationKind.PlayIntegrity => RelayDeviceAttestationKinds.PlayIntegrity,
        DeviceAttestationKind.BoundKey => RelayDeviceAttestationKinds.BoundKey,
        _ => RelayDeviceAttestationKinds.None
    };

    /// <summary>
    /// App Attest and bound-key devices prove possession on every credential call: a fresh API nonce and an assertion
    /// (App Attest) or ECDSA signature (bound key) whose client data is "{deviceId}|{nonce}", hashed with SHA-256 by the
    /// provider. Other devices send nothing.
    /// </summary>
    private async Task<RelayDeviceAssertion?> CreateAssertionAsync(PushRelayDeviceCredential credential, CancellationToken cancellationToken)
    {
        if (credential.AttestationKind is not (DeviceAttestationKind.AppAttest or DeviceAttestationKind.BoundKey)
            || _attestation is null || _attestation.Kind != credential.AttestationKind || string.IsNullOrEmpty(credential.AttestationKeyId))
            return null;

        var challenge = await _apiClient.CreateRelayDeviceChallengeAsync(credential.Platform, cancellationToken).ConfigureAwait(false);
        if (!challenge.IsSuccess || challenge.Result is not { } nonce || string.IsNullOrWhiteSpace(nonce.Nonce))
            throw MapFailure(challenge.IsSuccess ? WinoAccountClientErrorCodes.InvalidServiceResponse : challenge.ErrorCode, devicePath: true);

        var clientData = Encoding.UTF8.GetBytes($"{credential.DeviceId:D}|{nonce.Nonce}");
        var assertion = await _attestation.CreateAssertionAsync(credential.AttestationKeyId, clientData, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(assertion) ? null : new RelayDeviceAssertion(nonce.Nonce, assertion);
    }

    private Task<WinoAccountSession?> CaptureSessionAsync(CancellationToken cancellationToken)
        => _sessions is null ? Task.FromResult<WinoAccountSession?>(null) : _sessions.CaptureAsync(cancellationToken);

    private async Task<PushRelayAccessToken> FetchAsync(int generation)
    {
        await Task.Yield();
        try
        {
            using var timeout = new CancellationTokenSource(FetchTimeout);
            var token = await FetchCoreAsync(timeout.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _cached = token;
                    if (token.MailboxLimit is { } limit)
                        _mailboxLimit = limit;
                }
            }

            return token;
        }
        catch (OperationCanceledException ex)
        {
            throw new PushRelayApiException(new PushRelayProblem(PushRelayErrorCodes.ServiceUnavailable, 0, Detail: "Relay token request timed out."), ex);
        }
        finally
        {
            lock (_gate)
            {
                if (generation == _generation)
                    _pending = null;
            }
        }
    }

    private async Task<PushRelayAccessToken> FetchCoreAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var credential = await GetDeviceCredentialAsync(cancellationToken).ConfigureAwait(false);
            var session = await CaptureSessionAsync(cancellationToken).ConfigureAwait(false);

            // Desktop never uses the Wino Account's token: its device token is bound to this machine's key.
            // Mobile device path: no link yet, or the signed-in account is not the linked one (the link call will follow).
            var desktop = IsDesktop;
            var devicePath = credential is not null
                && (desktop || credential.LinkedWinoUserId is null || session is null || session.AccountId != credential.LinkedWinoUserId);
            if (!devicePath && !desktop && session is not null)
            {
                var userEnvelope = await _apiClient.CreateRelayTokenAsync(cancellationToken).ConfigureAwait(false);
                if (userEnvelope.IsSuccess && userEnvelope.Result is { } userResult && !string.IsNullOrWhiteSpace(userResult.Token))
                    return ToToken(userResult);

                throw MapFailure(userEnvelope.IsSuccess ? WinoAccountClientErrorCodes.InvalidServiceResponse : userEnvelope.ErrorCode, devicePath: false);
            }

            if (credential is null)
            {
                // Desktop never asks for a Wino Account: the device credential is missing for now (API device cap, unreachable API).
                throw new PushRelayApiException(desktop
                    ? new PushRelayProblem(PushRelayErrorCodes.ServiceUnavailable, 0, Detail: "The relay device credential is not available yet.")
                    : new PushRelayProblem(PushRelayErrorCodes.SignInRequired, 0, Detail: "No device credential and no Wino Account session."));
            }

            var assertion = await CreateAssertionAsync(credential, cancellationToken).ConfigureAwait(false);
            var envelope = await _apiClient.CreateRelayTokenForDeviceAsync(credential.DeviceId, credential.Secret, assertion, cancellationToken).ConfigureAwait(false);
            if (envelope.IsSuccess && envelope.Result is { } result && !string.IsNullOrWhiteSpace(result.Token))
                return ToToken(result);

            if (IsDeviceGone(envelope.ErrorCode) && attempt == 0)
            {
                // The API or the relay forgot this device: register again once.
                _logger.Information("The API no longer knows this relay device ({ErrorCode}); registering again.", envelope.ErrorCode);
                await ForgetDeviceAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw MapFailure(envelope.IsSuccess ? WinoAccountClientErrorCodes.InvalidServiceResponse : envelope.ErrorCode, devicePath: true);
        }
    }

    private static PushRelayAccessToken ToToken(RelayTokenDto result)
        => new(result.Token, result.ExpiresUtc, ResolveBaseUri(result.RelayUrl), ReadMailboxLimit(result.Token));

    private static bool IsDeviceGone(string? errorCode)
        => errorCode is RelayDeviceErrorCodes.DeviceRevoked or RelayDeviceErrorCodes.DeviceNotFound
            || errorCode?.StartsWith("HTTP 401", StringComparison.Ordinal) == true
            || errorCode?.StartsWith("HTTP 404", StringComparison.Ordinal) == true;

    private PushRelayApiException MapFailure(string? errorCode, bool devicePath)
    {
        if (!devicePath && (errorCode is WinoAccountClientErrorCodes.SignInRequired or WinoAccountClientErrorCodes.AccountSessionChanged
            || errorCode?.StartsWith("HTTP 401", StringComparison.Ordinal) == true))
        {
            return new PushRelayApiException(new PushRelayProblem(PushRelayErrorCodes.SignInRequired, 0, Detail: errorCode));
        }

        // RELAY_UNAVAILABLE (no signing key), an unreachable API and anything unexpected are retried with backoff.
        _logger.Warning("Relay token request ({Path}) failed with error code {ErrorCode}.", devicePath ? "device" : "user", errorCode);
        return new PushRelayApiException(new PushRelayProblem(PushRelayErrorCodes.ServiceUnavailable, 0, Detail: errorCode));
    }

    internal static Uri ResolveBaseUri(string? relayUrl)
    {
        if (!string.IsNullOrWhiteSpace(relayUrl)
            && Uri.TryCreate(relayUrl.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            var text = uri.GetLeftPart(UriPartial.Path);
            return new Uri(text.EndsWith('/') ? text : text + "/");
        }

        return new Uri(PushRelayDefaults.DefaultBaseUrl);
    }

    /// <summary>Reads the <c>wr_mbx</c> claim for display only; the relay enforces the limit itself.</summary>
    internal static int? ReadMailboxLimit(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
                return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            if (!document.RootElement.TryGetProperty("wr_mbx", out var claim))
                return null;

            if (claim.ValueKind == JsonValueKind.Number && claim.TryGetInt32(out var number) && number > 0)
                return number;

            return claim.ValueKind == JsonValueKind.String && int.TryParse(claim.GetString(), out var parsed) && parsed > 0 ? parsed : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
