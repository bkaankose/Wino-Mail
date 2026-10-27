#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;
using Wino.Core.Domain.Models.Settings;
using Wino.Messaging.UI;

namespace Wino.Services.PushRelay;

/// <summary>
/// Keeps this device registered with WinoRelay for the signed-in Wino Account. One worker runs passes; reasons that
/// arrive during a pass are coalesced into one more pass. Failures back off with a persisted schedule.
/// Platform adapters are optional: when a head registers no identity store or device info, the relay is unavailable
/// on that platform and nothing is sent.
/// </summary>
public sealed class PushRelayRegistrationService : IPushRelayRegistrationService,
    IRecipient<AccountCreatedMessage>, IRecipient<AccountRemovedMessage>, IRecipient<AccountUpdatedMessage>,
    IRecipient<WinoAccountSignedInMessage>, IRecipient<WinoAccountSignedOutMessage>,
    IDisposable
{
    internal static readonly TimeSpan CheckInInterval = TimeSpan.FromHours(20);
    internal static readonly TimeSpan TimerInterval = TimeSpan.FromHours(1);
    internal static readonly TimeSpan OptOutDeleteBudget = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan SignOutDeleteBudget = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultRateLimitDelay = TimeSpan.FromSeconds(60);
    internal const int ServiceUnavailableFailureThreshold = 3;

    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30), TimeSpan.FromHours(1)
    ];

    private static readonly PushRelaySyncReason[] ForcingReasons =
    [
        PushRelaySyncReason.UserRequested, PushRelaySyncReason.OptInChanged,
        PushRelaySyncReason.DeviceRevoked, PushRelaySyncReason.WinoAccountChanged
    ];

    private readonly IPushRelayClient _client;
    private readonly IPushRelayTokenProvider _tokens;
    private readonly IPushRelayStateStore _stateStore;
    private readonly IWinoAccountSessionService _sessions;
    private readonly IAccountService _accountService;
    private readonly IPushRelayMailboxRegistrationBuilder[] _builders;
    private readonly IPlatformCapabilities _capabilities;
    private readonly IAppMetadataService _metadata;
    private readonly ITranslationService? _translations;
    private readonly IMessenger? _messenger;
    private readonly IPushRelayDeviceIdentityStore? _identityStore;
    private readonly IPushRelayDeviceInfo? _deviceInfo;
    private readonly IPushTokenProvider? _pushTokens;
    private readonly IPushRelaySocketKeyStore? _socketKeys;
    private readonly IPreferencesService? _preferences;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger = Log.ForContext<PushRelayRegistrationService>();

    private readonly object _gate = new();
    private readonly SemaphoreSlim _passLock = new(1, 1);
    private readonly HashSet<PushRelaySyncReason> _pending = [];
    private readonly CancellationTokenSource _lifetime = new();
    private Task _worker = Task.CompletedTask;
    private Task _statusReports = Task.CompletedTask;
    private bool _workerRunning;
    private bool _started;
    private bool _signInRejected;
    private CancellationTokenSource? _currentPass;
    private PushRelayRegistrationSnapshot _snapshot;

    public PushRelayRegistrationService(
        IPushRelayClient client,
        IPushRelayTokenProvider tokens,
        IPushRelayStateStore stateStore,
        IWinoAccountSessionService sessions,
        IAccountService accountService,
        IEnumerable<IPushRelayMailboxRegistrationBuilder> builders,
        IPlatformCapabilities capabilities,
        IAppMetadataService metadata,
        ITranslationService? translations = null,
        IMessenger? messenger = null,
        IPushRelayDeviceIdentityStore? identityStore = null,
        IPushRelayDeviceInfo? deviceInfo = null,
        IPushTokenProvider? pushTokens = null,
        TimeProvider? timeProvider = null,
        IPushRelaySocketKeyStore? socketKeys = null,
        IPreferencesService? preferences = null)
    {
        _preferences = preferences;
        _client = client;
        _tokens = tokens;
        _stateStore = stateStore;
        _sessions = sessions;
        _accountService = accountService;
        _builders = builders.ToArray();
        _capabilities = capabilities;
        _metadata = metadata;
        _translations = translations;
        _messenger = messenger;
        _identityStore = identityStore;
        _deviceInfo = deviceInfo;
        _pushTokens = pushTokens;
        _socketKeys = socketKeys;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var optedIn = IsPlatformSupported && IsRelaySelected;
        _snapshot = PushRelayRegistrationSnapshot.Initial(
            !IsPlatformSupported ? PushRelayAvailability.UnsupportedPlatform
                : IsOptInPlatform && !optedIn ? PushRelayAvailability.DisabledByUser
                : PushRelayAvailability.Enabled,
            IsOptInPlatform, optedIn);
    }

    public PushRelayRegistrationSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public event EventHandler<PushRelayRegistrationSnapshot>? SnapshotChanged;

    public bool IsUserOptedIn => Snapshot.IsUserOptedIn;

    /// <summary>The head registered the relay adapters and its capabilities allow the relay (pushes, or the desktop socket).</summary>
    private bool IsPlatformSupported => _identityStore is not null && _deviceInfo is not null
        && (_capabilities.RemoteNotifications || IsOptInPlatform);

    /// <summary>Desktop: the app-wide sync method decides whether the relay is used.</summary>
    private bool IsOptInPlatform => SettingAvailability.IsAvailable(PlatformSetting.SyncMethod, _capabilities);

    /// <summary>Relay-based sync is selected (always true where the platform offers no choice).</summary>
    private bool IsRelaySelected => !IsOptInPlatform || _preferences is null || _preferences.SyncMethod == SyncMethod.Relay;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_started || _lifetime.IsCancellationRequested)
                return Task.CompletedTask;
            _started = true;
        }

        if (!IsPlatformSupported)
        {
            Publish(PushRelayAvailability.UnsupportedPlatform, _stateStore.Load(), null);
            return Task.CompletedTask;
        }

        if (_messenger is { } messenger)
        {
            messenger.Register<AccountCreatedMessage>(this);
            messenger.Register<AccountRemovedMessage>(this);
            messenger.Register<AccountUpdatedMessage>(this);
            messenger.Register<WinoAccountSignedInMessage>(this);
            messenger.Register<WinoAccountSignedOutMessage>(this);
        }

        if (_pushTokens is { } pushTokens)
            pushTokens.TokenChanged += PushTokenChanged;

        if (_preferences is { } preferences)
            preferences.PreferenceChanged += PreferenceChanged;

        _ = Task.Run(() => RunCheckInTimerAsync(_lifetime.Token));
        RequestSync(PushRelaySyncReason.Startup);
        return Task.CompletedTask;
    }

    public void RequestSync(PushRelaySyncReason reason)
    {
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested)
                return;

            _pending.Add(reason);

            // Before StartAsync the runtime may not have opened the database yet (iOS raises its
            // foreground event during launch). Keep the reason; the Startup request runs it.
            if (!_started || _workerRunning)
                return;

            _workerRunning = true;
            _worker = Task.Run(RunWorkerAsync);
        }
    }

    public async Task SyncNowAsync(PushRelaySyncReason reason, CancellationToken cancellationToken = default)
    {
        HashSet<PushRelaySyncReason> reasons;
        lock (_gate)
        {
            reasons = [.. _pending, reason];
            _pending.Clear();
        }

        await RunPassExclusiveAsync(reasons, cancellationToken).ConfigureAwait(false);
    }

    public Task CheckInAsync(CancellationToken cancellationToken = default)
        => SyncNowAsync(PushRelaySyncReason.CheckIn, cancellationToken);

    public async Task SetUserOptInAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!IsPlatformSupported)
            return;

        // The switch is the app-wide sync method; the preference change event queues the pass.
        var method = enabled ? SyncMethod.Relay : SyncMethod.Timer;
        if (_preferences is { } preferences && preferences.SyncMethod != method)
        {
            preferences.SyncMethod = method;
            _logger.Information("Sync method set to {Method} by the user.", method);
        }

        await SyncNowAsync(PushRelaySyncReason.OptInChanged, cancellationToken).ConfigureAwait(false);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        if (!IsPlatformSupported || _identityStore is not { } identityStore)
            return;

        // A running pass must not keep the sign-out waiting, and must not register again afterwards.
        lock (_gate)
        {
            _pending.Clear();
            _currentPass?.Cancel();
        }

        var locked = false;
        try
        {
            try { locked = await _passLock.WaitAsync(SignOutDeleteBudget, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }

            // The device keeps its identity, registrations and credentials; only the Wino Account comes off it.
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(SignOutDeleteBudget);
            try { await _tokens.UnlinkDeviceAsync(bounded.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.Information("Unlinking the relay device ran out of time; the API's sign-out covers it.");
            }

            _signInRejected = false;
            Publish(Snapshot.Availability, _stateStore.Load(), null, keepDevice: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.Warning(ex, "Relay sign-out did not finish cleanly.");
        }
        finally
        {
            if (locked)
                _passLock.Release();
        }
    }

    public async Task ReleaseAccountAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (_builders.FirstOrDefault(x => x.ProviderType == account.ProviderType) is not { } builder)
            return;

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(OptOutDeleteBudget);
        await ReleaseProviderRegistrationAsync(builder, account, bounded.Token, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PushRelayPushTestResult> SendPushTestAsync(CancellationToken cancellationToken = default)
    {
        if (!IsPlatformSupported || _identityStore is not { } identityStore)
            return PushRelayPushTestResult.NotRegistered;

        var identity = await identityStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (identity is null || Snapshot.Availability != PushRelayAvailability.Enabled)
            return PushRelayPushTestResult.NotRegistered;

        try
        {
            await _client.SendPushTestAsync(identity.DeviceId, cancellationToken).ConfigureAwait(false);
            return PushRelayPushTestResult.Queued;
        }
        catch (PushRelayApiException ex) when (ex.Status == 409)
        {
            return PushRelayPushTestResult.NoPushToken;
        }
        catch (PushRelayApiException ex)
        {
            if (ex.IsDeviceRevoked)
                RequestSync(PushRelaySyncReason.DeviceRevoked);

            _logger.Information("Relay test push failed ({Code}).", ex.Code);
            return ex.IsRateLimited ? PushRelayPushTestResult.RateLimited : PushRelayPushTestResult.Failed;
        }
    }

    public void ReportMailboxStatus(Guid accountId, string? status, string? reason, string? message)
        => QueueStatusUpdate(state =>
        {
            if (!state.Mailboxes.TryGetValue(accountId, out var entry))
                return false;

            var previousStatus = entry.Status;
            var previousError = entry.LastErrorCode;
            entry.Status = status ?? entry.Status;
            entry.MonitoringReason = reason;
            entry.MonitoringMessage = message;
            entry.UpdatedUtc = UtcNow;

            var needsReauth = status == PushRelayMailboxStatuses.NeedsReauth;
            if (needsReauth)
            {
                // The relay can no longer sign in: upload the current credentials again on the next pass.
                entry.CredentialFingerprint = null;
                entry.LastErrorCode = null;
            }

            LogMailboxTransition(accountId, previousStatus, previousError, entry);
            return needsReauth;
        });

    public void ReportTestPush()
        => QueueStatusUpdate(state =>
        {
            state.LastTestPushUtc = UtcNow;
            return false;
        });

    /// <summary>Completes when no pass, queued pass or status update is pending. Used by tests.</summary>
    internal async Task WaitForIdleAsync()
    {
        while (true)
        {
            Task worker, reports;
            lock (_gate)
            {
                worker = _worker;
                reports = _statusReports;
                if (!_workerRunning && worker.IsCompleted && reports.IsCompleted)
                    return;
            }

            await Task.WhenAll(worker, reports).ConfigureAwait(false);
        }
    }

    public void Receive(AccountCreatedMessage message) => RequestSync(PushRelaySyncReason.AccountsChanged);
    public void Receive(AccountRemovedMessage message) => RequestSync(PushRelaySyncReason.AccountsChanged);
    public void Receive(AccountUpdatedMessage message) => RequestSync(PushRelaySyncReason.CredentialsChanged);

    public void Receive(WinoAccountSignedInMessage message)
    {
        _signInRejected = false;

        // Timer mode asks the API for nothing: the pass only refreshes the published state.
        if (!IsRelaySelected)
        {
            RequestSync(PushRelaySyncReason.WinoAccountChanged);
            return;
        }

        _ = LinkThenSyncAsync();
    }

    public void Receive(WinoAccountSignedOutMessage message) => RequestSync(PushRelaySyncReason.WinoAccountChanged);

    /// <summary>A new Wino Account adopts this device at the API; the relay learns the owner from the API.</summary>
    private async Task LinkThenSyncAsync()
    {
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            bounded.CancelAfter(TimeSpan.FromSeconds(20));
            await _tokens.LinkDeviceAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            _logger.Information(ex, "Linking the relay device after sign-in did not finish; the next pass tries again.");
        }

        RequestSync(PushRelaySyncReason.WinoAccountChanged);
    }

    private void PreferenceChanged(object? sender, string propertyName)
    {
        if (propertyName != nameof(IPreferencesService.SyncMethod))
            return;

        // Turned off: publish at once, without the device, so the socket never reconnects with the identity the
        // opt-out pass is about to delete. Turning it on again waits for the pass that registers after that one.
        if (IsOptInPlatform && !IsRelaySelected)
            Publish(PushRelayAvailability.DisabledByUser, _stateStore.Load(), null);

        RequestSync(PushRelaySyncReason.OptInChanged);
    }

    private void PushTokenChanged(object? sender, EventArgs e) => RequestSync(PushRelaySyncReason.PushTokenChanged);

    private async Task RunCheckInTimerAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimerInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                RequestSync(PushRelaySyncReason.CheckIn);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunWorkerAsync()
    {
        while (true)
        {
            HashSet<PushRelaySyncReason> reasons;
            lock (_gate)
            {
                if (_pending.Count == 0 || _lifetime.IsCancellationRequested)
                {
                    _pending.Clear();
                    _workerRunning = false;
                    return;
                }

                reasons = [.. _pending];
                _pending.Clear();
            }

            try
            {
                await RunPassExclusiveAsync(reasons, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Sign-out or shutdown cancelled the pass.
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Relay registration pass failed unexpectedly.");
            }
        }
    }

    private async Task RunPassExclusiveAsync(HashSet<PushRelaySyncReason> reasons, CancellationToken cancellationToken)
    {
        await _passLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var pass = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        lock (_gate) _currentPass = pass;
        try
        {
            await RunPassAsync(reasons, pass.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_currentPass, pass))
                    _currentPass = null;
            }

            _passLock.Release();
        }
    }

    private async Task RunPassAsync(HashSet<PushRelaySyncReason> reasons, CancellationToken cancellationToken)
    {
        var state = _stateStore.Load();

        // A schedule persisted by an earlier version (up to 24 h) is clamped to the current longest backoff, so an
        // upgrade never waits a day for the first retry.
        if (state.NextAttemptUtc is { } scheduled && scheduled > UtcNow + Backoff[^1])
        {
            state.NextAttemptUtc = UtcNow + Backoff[^1];
            _stateStore.Save(state);
            _logger.Information("Relay registration retry moved earlier to {NextAttemptUtc:O} (previous schedule exceeded the {Max} backoff cap).", state.NextAttemptUtc, Backoff[^1]);
        }

        if (!IsPlatformSupported || _identityStore is not { } identityStore || _deviceInfo is not { } deviceInfo)
        {
            Publish(PushRelayAvailability.UnsupportedPlatform, state, null);
            return;
        }

        var session = await _sessions.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var identity = await identityStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        // Timer mode asks the API for nothing: the device credential is only created once the relay is wanted.
        // A registered device still authenticates once so turning the relay off removes it at the relay.
        var canAuthenticate = (IsRelaySelected || identity is not null) && await CanAuthenticateAsync(cancellationToken).ConfigureAwait(false);
        var gate = EvaluateGate(state, canAuthenticate);

        if (gate != PushRelayAvailability.Enabled)
        {
            if (identity is not null && gate is PushRelayAvailability.SignInRequired or PushRelayAvailability.DisabledByUser)
            {
                _logger.Information("Live notifications are {Availability}; removing this relay device.", gate);
                await ReleaseProviderRegistrationsAsync(OptOutDeleteBudget, cancellationToken).ConfigureAwait(false);
                if (canAuthenticate)
                    await DeleteRemoteDeviceAsync(identity, OptOutDeleteBudget, cancellationToken).ConfigureAwait(false);

                await ForgetDeviceAsync(cancellationToken).ConfigureAwait(false);
                state = _stateStore.Load();
            }

            Publish(gate, state, null);
            return;
        }

        // A signed-in account that is not yet the device's owner gets linked before the pass registers anything.
        if (session is not null)
        {
            try { await _tokens.LinkDeviceAsync(cancellationToken).ConfigureAwait(false); }
            catch (PushRelayApiException ex) { _logger.Information("Linking the relay device failed ({Code}); continuing on the device path.", ex.Code); }
        }

        var identityReplaced = false;
        if (identity is not null && await HasReplacedDeviceCredentialAsync(identity, cancellationToken).ConfigureAwait(false))
        {
            // The relay device is registered under the API device id, so the old identity cannot be reused.
            _logger.Information("The relay device credential was replaced; registering the relay device again.");
            await identityStore.DeleteAsync(cancellationToken).ConfigureAwait(false);
            identity = null;
            state.Mailboxes.Clear();
            state.LastCheckInUtc = null;
            identityReplaced = true;
        }

        var now = UtcNow;
        var forced = identityReplaced || reasons.Overlaps(ForcingReasons);
        if (!forced && state.NextAttemptUtc is { } next && next > now)
        {
            Publish(ComputeAvailability(state, true), state, identity);
            return;
        }

        var mailboxesBefore = CaptureMailboxes(state);
        try
        {
            identity = await RunEnabledPassAsync(state, session, identity, identityStore, deviceInfo.Describe(), reasons, now, cancellationToken)
                .ConfigureAwait(false);

            if (!state.PlatformRejected)
            {
                state.FailureCount = 0;
                state.NextAttemptUtc = null;
                state.LastErrorCode = null;
                state.LastErrorDetail = null;
                state.MailboxLimit = _tokens.MailboxLimit ?? state.MailboxLimit;
            }

            _signInRejected = false;
        }
        catch (PushRelayApiException ex)
        {
            RecordPassFailure(state, ex, now);
        }

        _stateStore.Save(state);
        LogMailboxTransitions(mailboxesBefore, state);
        Publish(ComputeAvailability(state, true), state, identity);
    }

    private static Dictionary<Guid, (string? Status, string? ErrorCode)> CaptureMailboxes(PushRelayState state)
        => state.Mailboxes.ToDictionary(x => x.Key, x => (x.Value.Status, x.Value.LastErrorCode));

    /// <summary>One line per mailbox whose status or error code changed during the pass.</summary>
    private void LogMailboxTransitions(Dictionary<Guid, (string? Status, string? ErrorCode)> before, PushRelayState state)
    {
        foreach (var (accountId, entry) in state.Mailboxes)
        {
            var (previousStatus, previousError) = before.TryGetValue(accountId, out var previous) ? previous : (null, null);
            LogMailboxTransition(accountId, previousStatus, previousError, entry);
        }
    }

    private void LogMailboxTransition(Guid accountId, string? previousStatus, string? previousError, PushRelayMailboxRegistration entry)
    {
        if (previousStatus == entry.Status && previousError == entry.LastErrorCode)
            return;

        _logger.Information("Relay mailbox {AccountId} status {Previous} -> {Current} ({ErrorCode}).",
            accountId, previousStatus, entry.Status, entry.LastErrorCode);
    }

    private async Task<PushRelayDeviceIdentity?> RunEnabledPassAsync(PushRelayState state, WinoAccountSession? session,
        PushRelayDeviceIdentity? identity, IPushRelayDeviceIdentityStore identityStore, PushRelayDeviceDescriptor descriptor,
        HashSet<PushRelaySyncReason> reasons, DateTime now, CancellationToken cancellationToken)
    {
        var name = Limit(descriptor.Name);
        var osVersion = Limit(descriptor.OsVersion);
        var pushToken = CurrentPushToken;
        var pushTokenHash = Hash(pushToken);
        var appVersion = Normalize(_metadata.AppVersion);
        var appBuild = Normalize(_metadata.SentryDist);
        var locale = ResolveLocale();
        var socketKey = await LoadSocketPublicKeyAsync(cancellationToken).ConfigureAwait(false);
        var socketKeyHash = Hash(socketKey);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var deviceFresh = false;
                List<PushRelayMailbox>? serverView = null;

                if (identity is null)
                {
                    state.Mailboxes.Clear();
                    var credential = await _tokens.GetDeviceCredentialAsync(cancellationToken).ConfigureAwait(false);
                    PushRelayRegisterDeviceResponse registration;
                    try
                    {
                        try
                        {
                            registration = await _client.RegisterDeviceAsync(
                                new PushRelayRegisterDeviceRequest(descriptor.Platform, pushToken, appVersion, appBuild, locale, osVersion, name, socketKey, credential?.DeviceId),
                                cancellationToken).ConfigureAwait(false);
                        }
                        catch (PushRelayApiException ex) when (ex.IsDeviceRevoked && credential is not null)
                        {
                            // The relay revoked the API device behind this credential: register a new one with the API, then try once more.
                            _logger.Information("The relay revoked this API device; registering a new one.");
                            await _tokens.ForgetDeviceAsync(cancellationToken).ConfigureAwait(false);
                            credential = await _tokens.GetDeviceCredentialAsync(cancellationToken).ConfigureAwait(false);
                            registration = await _client.RegisterDeviceAsync(
                                new PushRelayRegisterDeviceRequest(descriptor.Platform, pushToken, appVersion, appBuild, locale, osVersion, name, socketKey, credential?.DeviceId),
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (PushRelayApiException ex) when (ex.Status == 400 && ex.Code == PushRelayErrorCodes.InvalidRequest)
                    {
                        // Windows today. Retrying the same request cannot succeed; try again with the next app version.
                        _logger.Information("The relay rejected platform {Platform} for app version {AppVersion}.", descriptor.Platform, appVersion);
                        state.PlatformRejected = true;
                        state.PlatformRejectedAppVersion = appVersion;
                        state.LastErrorCode = ex.Code;
                        state.LastErrorDetail = ex.Problem.Detail;
                        return null;
                    }

                    identity = new PushRelayDeviceIdentity(registration.DeviceId, session?.AccountId, descriptor.Platform, now);
                    // The identity is saved before the state so a crash never leaves state for an unknown device.
                    await identityStore.SaveAsync(identity, cancellationToken).ConfigureAwait(false);
                    state.PlatformRejected = false;
                    state.PlatformRejectedAppVersion = null;
                    RememberDevice(state, pushTokenHash, appVersion, appBuild, locale, name, osVersion, socketKeyHash);
                    state.LastCheckInUtc = now;
                    _stateStore.Save(state);
                    deviceFresh = true;
                    _logger.Information("Registered relay device for platform {Platform}.", descriptor.Platform);
                }
                else if (state.LastPushTokenHash != pushTokenHash || state.LastAppVersion != appVersion || state.LastAppBuild != appBuild
                    || state.LastLocale != locale || state.LastName != name || state.LastOsVersion != osVersion
                    || (state.LastSocketKeyHash ?? string.Empty) != socketKeyHash)
                {
                    var device = await _client.UpdateDeviceAsync(identity.DeviceId,
                        new PushRelayUpdateDeviceRequest(pushToken, appVersion, appBuild, locale, osVersion, name, socketKey), cancellationToken).ConfigureAwait(false);
                    RememberDevice(state, pushTokenHash, appVersion, appBuild, locale, name, osVersion, socketKeyHash);
                    state.LastCheckInUtc = now;
                    serverView = device.Mailboxes ?? [];
                    deviceFresh = true;
                }
                else if (state.LastCheckInUtc is not { } lastCheckIn || now - lastCheckIn >= CheckInInterval
                    || reasons.Contains(PushRelaySyncReason.UserRequested) || reasons.Contains(PushRelaySyncReason.DeviceRevoked))
                {
                    var device = await _client.GetDeviceAsync(identity.DeviceId, cancellationToken).ConfigureAwait(false);
                    state.LastCheckInUtc = now;

                    // The relay invalidates a token that another device registered later; send ours again.
                    // Desktop devices never have one (socket-only), so they never send one.
                    if (!device.PushTokenRegistered && pushToken is not null)
                    {
                        _logger.Information("The relay has no valid push token for this device ({Reason}); sending it again.", device.PushTokenInvalidReason);
                        state.LastPushTokenHash = null;
                        device = await _client.UpdateDeviceAsync(identity.DeviceId,
                            new PushRelayUpdateDeviceRequest(pushToken, appVersion, appBuild, locale, osVersion, name, socketKey), cancellationToken).ConfigureAwait(false);
                        RememberDevice(state, pushTokenHash, appVersion, appBuild, locale, name, osVersion, socketKeyHash);
                    }

                    serverView = device.Mailboxes ?? [];
                    deviceFresh = true;
                }

                await SyncMailboxesAsync(state, identity, serverView, deviceFresh, reasons, now, cancellationToken).ConfigureAwait(false);
                return identity;
            }
            catch (PushRelayApiException ex) when (ex.IsDeviceRevoked && attempt == 0 && identity is not null)
            {
                _logger.Information("The relay revoked this device; registering again.");
                await identityStore.DeleteAsync(cancellationToken).ConfigureAwait(false);
                identity = null;
                state.Mailboxes.Clear();
                state.LastCheckInUtc = null;
            }
        }
    }

    private async Task SyncMailboxesAsync(PushRelayState state, PushRelayDeviceIdentity identity, List<PushRelayMailbox>? serverView,
        bool deviceFresh, HashSet<PushRelaySyncReason> reasons, DateTime now, CancellationToken cancellationToken)
    {
        var registrations = await BuildRegistrationsAsync(cancellationToken).ConfigureAwait(false);
        var eligible = registrations.Select(x => x.Account.Id).ToHashSet();
        var retryFailures = deviceFresh || reasons.Contains(PushRelaySyncReason.UserRequested);

        var accountsChanged = !eligible.SetEquals(state.Mailboxes.Keys);
        foreach (var stale in state.Mailboxes.Keys.Where(id => !eligible.Contains(id)).ToList())
            state.Mailboxes.Remove(stale);

        if (deviceFresh || accountsChanged)
        {
            var ids = registrations.Select(x => x.Account.Id.ToString("D")).ToList();
            var response = await _client.SyncAccountsAsync(identity.DeviceId, ids, cancellationToken).ConfigureAwait(false);
            if (response.Removed > 0)
                _logger.Information("The relay dropped {Count} subscriptions for removed accounts.", response.Removed);
            serverView = response.Mailboxes ?? [];
        }

        var serverByAccount = new Dictionary<Guid, PushRelayMailbox>();
        if (serverView is not null)
        {
            foreach (var mailbox in serverView)
            {
                if (Guid.TryParse(mailbox.ClientAccountId, out var accountId))
                    serverByAccount.TryAdd(accountId, mailbox);
            }

            // The relay's view is complete: a subscription it no longer lists has to be created again.
            foreach (var (accountId, entry) in state.Mailboxes)
            {
                if (entry.SubscriptionId is { } subscriptionId && serverView.FirstOrDefault(x => x.Id == subscriptionId) is { } match)
                    Apply(entry, match, now);
                else if (serverByAccount.TryGetValue(accountId, out var adopted))
                    Adopt(entry, adopted, now);
                else if (entry.SubscriptionId is not null)
                {
                    entry.SubscriptionId = null;
                    entry.CredentialFingerprint = null;
                    entry.Status = null;
                }
            }
        }

        var limitReached = false;
        foreach (var (account, registration) in registrations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!state.Mailboxes.TryGetValue(account.Id, out var entry))
            {
                entry = new PushRelayMailboxRegistration { UpdatedUtc = now };
                state.Mailboxes[account.Id] = entry;
                if (serverByAccount.TryGetValue(account.Id, out var adopted))
                    Adopt(entry, adopted, now);
            }

            var failedWithSameCredentials = entry.LastErrorCode is not null && entry.CredentialFingerprint == registration.CredentialFingerprint;
            if (failedWithSameCredentials && !retryFailures)
                continue;

            if (entry.SubscriptionId is null)
            {
                if (limitReached)
                {
                    entry.LastErrorCode = PushRelayErrorCodes.MailboxLimitReached;
                    entry.CredentialFingerprint = registration.CredentialFingerprint;
                    entry.UpdatedUtc = now;
                    continue;
                }

                limitReached = await CreateMailboxAsync(identity, account, entry, registration, now, cancellationToken).ConfigureAwait(false);
            }
            else if (entry.CredentialFingerprint != registration.CredentialFingerprint || failedWithSameCredentials
                || entry.Status == PushRelayMailboxStatuses.NeedsReauth)
            {
                limitReached |= registration.IsAppManaged
                    ? await CreateMailboxAsync(identity, account, entry, registration, now, cancellationToken).ConfigureAwait(false)
                    : await UpdateCredentialsAsync(identity, account, entry, registration, now, allowCreate: true, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Returns true when the relay reported the mailbox limit.</summary>
    private async Task<bool> CreateMailboxAsync(PushRelayDeviceIdentity identity, MailAccount account, PushRelayMailboxRegistration entry,
        PushRelayMailboxRegistrationRequest registration, DateTime now, CancellationToken cancellationToken)
    {
        PushRelayMailbox mailbox;
        try
        {
            mailbox = await _client.CreateMailboxAsync(identity.DeviceId, registration.Create, cancellationToken).ConfigureAwait(false);
            Succeeded(entry, mailbox, registration, now);
        }
        catch (PushRelayApiException ex) when (ex.Code == PushRelayErrorCodes.MailboxAlreadyAdded && ex.Problem.SubscriptionId is { } subscriptionId)
        {
            entry.SubscriptionId = subscriptionId;
            return await UpdateCredentialsAsync(identity, account, entry, registration, now, allowCreate: false, cancellationToken).ConfigureAwait(false);
        }
        catch (PushRelayApiException ex) when (IsMailboxScoped(ex))
        {
            Failed(entry, ex, registration, now);
            return ex.Code == PushRelayErrorCodes.MailboxLimitReached;
        }

        // Registrations this one replaced that no other device uses: the provider would keep calling the relay for them.
        if (mailbox.ObsoleteExternalIds is { Count: > 0 } obsolete && _builders.FirstOrDefault(x => x.ProviderType == account.ProviderType) is { } builder)
        {
            _logger.Information("The relay retired {Count} provider registrations of account {AccountId}; deleting them.", obsolete.Count, account.Id);
            try { await builder.DeleteObsoleteRegistrationsAsync(account, obsolete, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Information(ex, "Retired provider registrations of account {AccountId} could not be deleted.", account.Id);
            }
        }

        return false;
    }

    private async Task<bool> UpdateCredentialsAsync(PushRelayDeviceIdentity identity, MailAccount account, PushRelayMailboxRegistration entry,
        PushRelayMailboxRegistrationRequest registration, DateTime now, bool allowCreate, CancellationToken cancellationToken)
    {
        if (entry.SubscriptionId is not { } subscriptionId)
            return false;

        try
        {
            var mailbox = await _client.UpdateMailboxCredentialsAsync(identity.DeviceId, subscriptionId, registration.Update, cancellationToken)
                .ConfigureAwait(false);
            Succeeded(entry, mailbox, registration, now);
            return false;
        }
        catch (PushRelayApiException ex) when (ex.Status == 404 && allowCreate)
        {
            entry.SubscriptionId = null;
            entry.CredentialFingerprint = null;
            entry.Status = null;
            return await CreateMailboxAsync(identity, account, entry, registration, now, cancellationToken).ConfigureAwait(false);
        }
        catch (PushRelayApiException ex) when (IsMailboxScoped(ex))
        {
            Failed(entry, ex, registration, now);
            return ex.Code == PushRelayErrorCodes.MailboxLimitReached;
        }
    }

    private async Task<List<(MailAccount Account, PushRelayMailboxRegistrationRequest Registration)>> BuildRegistrationsAsync(CancellationToken cancellationToken)
    {
        var result = new List<(MailAccount, PushRelayMailboxRegistrationRequest)>();
        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false) ?? [];
        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var builder = _builders.FirstOrDefault(x => x.ProviderType == account.ProviderType);
            if (builder is null)
                continue;

            try
            {
                if (await builder.BuildAsync(account, cancellationToken).ConfigureAwait(false) is { } registration)
                    result.Add((account, registration));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Relay registration for account {AccountId} could not be built.", account.Id);
            }
        }

        return result;
    }

    private void RecordPassFailure(PushRelayState state, PushRelayApiException ex, DateTime now)
    {
        // Desktop never needs a Wino Account, so a token failure there is retried with backoff like any other.
        if ((ex.IsSignInRequired || ex.IsTokenRejected) && !IsDesktopDevice)
        {
            _signInRejected = true;
            state.FailureCount++;
            state.NextAttemptUtc = now + BackoffFor(state.FailureCount);
            state.LastErrorCode = PushRelayErrorCodes.SignInRequired;
            state.LastErrorDetail = null;
        }
        else if (ex.IsRateLimited)
        {
            state.NextAttemptUtc = now + (ex.Problem.RetryAfterSeconds is { } seconds and > 0 ? TimeSpan.FromSeconds(seconds) : DefaultRateLimitDelay);
            state.LastErrorCode = ex.Code;
            state.LastErrorDetail = ex.Problem.Detail;
        }
        else
        {
            state.FailureCount++;
            state.NextAttemptUtc = now + BackoffFor(state.FailureCount);
            state.LastErrorCode = ex.Code;
            state.LastErrorDetail = ex.Problem.Detail;
        }

        _logger.Information("Relay registration pass failed ({Code}/{Reason}); next attempt at {NextAttemptUtc:O}.",
            ex.Code, ex.Problem.Reason, state.NextAttemptUtc);
    }

    /// <summary>A relay token can be issued: the device path works, or a Wino Account is signed in.</summary>
    private async Task<bool> CanAuthenticateAsync(CancellationToken cancellationToken)
    {
        try { return !await _tokens.RequiresSignInAsync(cancellationToken).ConfigureAwait(false); }
        catch (PushRelayApiException ex)
        {
            // The API could not be reached while registering the device: treat it as authenticable and let the pass back off.
            _logger.Information("Relay device registration is unavailable ({Code}); the pass will retry.", ex.Code);
            return true;
        }
    }

    private PushRelayAvailability EvaluateGate(PushRelayState state, bool canAuthenticate)
    {
        if (!IsPlatformSupported)
            return PushRelayAvailability.UnsupportedPlatform;
        if (IsOptInPlatform && !IsRelaySelected)
            return PushRelayAvailability.DisabledByUser;
        if (!canAuthenticate)
            return PushRelayAvailability.SignInRequired;
        if (state.PlatformRejected && state.PlatformRejectedAppVersion == Normalize(_metadata.AppVersion))
            return PushRelayAvailability.RejectedByRelay;
        return PushRelayAvailability.Enabled;
    }

    private PushRelayAvailability ComputeAvailability(PushRelayState state, bool canAuthenticate)
    {
        var gate = EvaluateGate(state, canAuthenticate);
        if (gate != PushRelayAvailability.Enabled)
            return gate;
        if (_signInRejected)
            return PushRelayAvailability.SignInRequired;
        return state.FailureCount >= ServiceUnavailableFailureThreshold ? PushRelayAvailability.ServiceUnavailable : gate;
    }

    private async Task DeleteRemoteDeviceAsync(PushRelayDeviceIdentity identity, TimeSpan budget, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(budget);
        try
        {
            await _client.DeleteDeviceAsync(identity.DeviceId, bounded.Token).ConfigureAwait(false);
            _logger.Information("Relay device removed.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Best effort: the relay reaper removes devices that stop calling.
            _logger.Information("Relay device could not be removed ({Error}).", ex is PushRelayApiException relay ? relay.Code : ex.GetType().Name);
        }
    }

    /// <summary>
    /// Live notifications are being turned off: every account's own provider registration (Graph subscription) is
    /// deleted so the provider stops notifying the relay. Bounded and best effort.
    /// </summary>
    private async Task ReleaseProviderRegistrationsAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        List<MailAccount> accounts;
        try { accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false) ?? []; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Accounts could not be read to release their provider registrations.");
            return;
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(budget);
        foreach (var account in accounts)
        {
            if (_builders.FirstOrDefault(x => x.ProviderType == account.ProviderType) is { } builder)
                await ReleaseProviderRegistrationAsync(builder, account, bounded.Token, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReleaseProviderRegistrationAsync(IPushRelayMailboxRegistrationBuilder builder, MailAccount account,
        CancellationToken bounded, CancellationToken cancellationToken)
    {
        try
        {
            await builder.ReleaseRegistrationAsync(account, bounded).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Best effort: the provider registration expires on its own.
            _logger.Information("The provider registration of account {AccountId} could not be released ({Error}).", account.Id, ex.GetType().Name);
        }
    }

    private async Task ForgetDeviceAsync(CancellationToken cancellationToken)
    {
        if (_identityStore is { } identityStore)
        {
            try { await identityStore.DeleteAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Relay device identity could not be deleted.");
            }
        }

        _stateStore.Clear();
    }

    private void QueueStatusUpdate(Func<PushRelayState, bool> update)
    {
        lock (_gate)
        {
            var previous = _statusReports;
            _statusReports = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                bool requestSync;
                await _passLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    var state = _stateStore.Load();
                    requestSync = update(state);
                    _stateStore.Save(state);
                    Publish(Snapshot.Availability, state, null, keepDevice: true);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Relay status update failed.");
                    return;
                }
                finally
                {
                    _passLock.Release();
                }

                if (requestSync)
                    RequestSync(PushRelaySyncReason.CredentialsChanged);
            });
        }
    }

    private void Publish(PushRelayAvailability availability, PushRelayState state, PushRelayDeviceIdentity? identity, bool keepDevice = false)
    {
        var deviceId = identity?.DeviceId ?? (keepDevice ? Snapshot.DeviceId : null);
        var mailboxes = state.Mailboxes
            .OrderBy(x => x.Key)
            .Select(x => new PushRelayMailboxSnapshot(x.Key, x.Value.SubscriptionId, x.Value.Status, x.Value.Muted,
                x.Value.MonitoringReason, x.Value.LastErrorCode))
            .ToList();

        var snapshot = new PushRelayRegistrationSnapshot(
            availability,
            IsOptInPlatform,
            IsRelaySelected,
            deviceId,
            CurrentPushToken is not null,
            state.LastCheckInUtc,
            state.NextAttemptUtc,
            state.LastErrorCode,
            state.LastErrorDetail,
            mailboxes,
            state.MailboxLimit ?? _tokens.MailboxLimit ?? PushRelayDefaults.DefaultMailboxLimit,
            state.LastTestPushUtc);

        var previous = Interlocked.Exchange(ref _snapshot, snapshot);
        if (previous.Availability != availability)
        {
            _logger.Information("Live notifications availability changed {Previous} -> {Current} (reason {Reason}, next attempt {NextAttemptUtc:O}).",
                previous.Availability, availability, state.LastErrorCode, state.NextAttemptUtc);
        }

        try
        {
            SnapshotChanged?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "A relay snapshot subscriber failed.");
        }
    }

    private static bool IsMailboxScoped(PushRelayApiException ex)
        => ex.Status is >= 400 and < 600 && !ex.IsDeviceRevoked && !ex.IsTokenRejected && !ex.IsRateLimited
           && !ex.IsSignInRequired && !ex.IsTransient && ex.Status != 401;

    private static void Succeeded(PushRelayMailboxRegistration entry, PushRelayMailbox mailbox, PushRelayMailboxRegistrationRequest registration, DateTime now)
    {
        Apply(entry, mailbox, now);
        entry.SubscriptionId = mailbox.Id;
        entry.CredentialFingerprint = registration.CredentialFingerprint;
        entry.LastErrorCode = null;
    }

    private static void Failed(PushRelayMailboxRegistration entry, PushRelayApiException ex, PushRelayMailboxRegistrationRequest registration, DateTime now)
    {
        // The attempted fingerprint is kept so the same credentials are not retried on every pass.
        entry.LastErrorCode = ex.Code;
        entry.CredentialFingerprint = registration.CredentialFingerprint;
        entry.UpdatedUtc = now;
    }

    private static void Adopt(PushRelayMailboxRegistration entry, PushRelayMailbox mailbox, DateTime now)
    {
        // Known to the relay but not to this state: upload the current credentials once.
        entry.SubscriptionId = mailbox.Id;
        entry.CredentialFingerprint = null;
        Apply(entry, mailbox, now);
    }

    private static void Apply(PushRelayMailboxRegistration entry, PushRelayMailbox mailbox, DateTime now)
    {
        entry.Status = mailbox.Status;
        entry.Muted = mailbox.Muted;
        entry.MonitoringReason = mailbox.Monitoring?.Reason;
        entry.MonitoringMessage = mailbox.Monitoring?.Message;
        entry.UpdatedUtc = now;
    }

    private static void RememberDevice(PushRelayState state, string pushTokenHash, string? appVersion, string? appBuild, string? locale,
        string? name, string? osVersion, string socketKeyHash)
    {
        state.LastPushTokenHash = pushTokenHash;
        state.LastAppVersion = appVersion;
        state.LastAppBuild = appBuild;
        state.LastLocale = locale;
        state.LastName = name;
        state.LastOsVersion = osVersion;
        state.LastSocketKeyHash = socketKeyHash;
    }

    /// <summary>
    /// The desktop socket public key (base64 SubjectPublicKeyInfo), created on first use. Heads without a key store
    /// (mobile) send none. A store failure is logged and the device registers without a key; the socket client reports
    /// <c>socket_key_required</c> later if the relay insists on one.
    /// </summary>
    private async Task<string?> LoadSocketPublicKeyAsync(CancellationToken cancellationToken)
    {
        if (_socketKeys is null)
            return null;

        try
        {
            return (await _socketKeys.GetOrCreateAsync(cancellationToken).ConfigureAwait(false)).PublicKeySpkiBase64;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "The relay socket key could not be loaded; registering the device without one.");
            return null;
        }
    }

    /// <summary>Desktop devices are socket-only: they never register a push token, so a closed app receives nothing.</summary>
    private string? CurrentPushToken
        => IsDesktopDevice ? null
            : _pushTokens is { IsPushChannelAvailable: true, CurrentToken: { Length: > 0 } token } ? token : null;

    private bool IsDesktopDevice => _deviceInfo?.Describe().Platform is PushRelayPlatforms.Windows or PushRelayPlatforms.MacOS;

    /// <summary>
    /// Desktop only: the device credential now names another API device than the stored relay identity (the bound-key
    /// upgrade, or a device the API registered again). Mobile identities may predate their credential and are left alone.
    /// </summary>
    private async Task<bool> HasReplacedDeviceCredentialAsync(PushRelayDeviceIdentity identity, CancellationToken cancellationToken)
    {
        if (!IsDesktopDevice)
            return false;

        try
        {
            return await _tokens.GetDeviceCredentialAsync(cancellationToken).ConfigureAwait(false) is { } credential
                && credential.DeviceId != identity.DeviceId;
        }
        catch (PushRelayApiException)
        {
            return false;
        }
    }

    private string? ResolveLocale()
    {
        try
        {
            var code = _translations?.CurrentLanguageModel?.Code;
            return Normalize(string.IsNullOrWhiteSpace(code) ? CultureInfo.CurrentUICulture.Name : code);
        }
        catch (Exception)
        {
            return Normalize(CultureInfo.CurrentUICulture.Name);
        }
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private static TimeSpan BackoffFor(int failureCount) => Backoff[Math.Clamp(failureCount - 1, 0, Backoff.Length - 1)];

    private static string Hash(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Limit(string? value)
    {
        var normalized = Normalize(value);
        return normalized is { Length: > PushRelayDefaults.MaxDeviceTextLength } ? normalized[..PushRelayDefaults.MaxDeviceTextLength] : normalized;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested)
                return;
            _lifetime.Cancel();
        }

        _messenger?.UnregisterAll(this);
        if (_pushTokens is { } pushTokens)
            pushTokens.TokenChanged -= PushTokenChanged;
    }
}
