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
        TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(24)
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
        IPushRelaySocketKeyStore? socketKeys = null)
    {
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

        var optedIn = IsPlatformSupported && _stateStore.Load().UserOptIn;
        _snapshot = PushRelayRegistrationSnapshot.Initial(
            !IsPlatformSupported ? PushRelayAvailability.UnsupportedPlatform
                : IsOptInPlatform && !optedIn ? PushRelayAvailability.DisabledByUser
                : PushRelayAvailability.SignInRequired,
            IsOptInPlatform, optedIn);
    }

    public PushRelayRegistrationSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public event EventHandler<PushRelayRegistrationSnapshot>? SnapshotChanged;

    public bool IsUserOptedIn => Snapshot.IsUserOptedIn;

    /// <summary>The head registered the relay adapters and its capabilities allow the relay.</summary>
    private bool IsPlatformSupported => _identityStore is not null && _deviceInfo is not null
        && (_capabilities.RemoteNotifications || _capabilities.PushRelayOptIn);

    private bool IsOptInPlatform => _capabilities.PushRelayOptIn;

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

        await _passLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _stateStore.Load();
            if (state.UserOptIn != enabled)
            {
                state.UserOptIn = enabled;
                _stateStore.Save(state);
                _logger.Information("Live notifications turned {State} by the user.", enabled ? "on" : "off");
            }

            Publish(Snapshot.Availability, state, null, keepDevice: true);
        }
        finally
        {
            _passLock.Release();
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

            PushRelayDeviceIdentity? identity = null;
            try { identity = await identityStore.LoadAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Relay device identity could not be read during sign-out.");
            }

            if (identity is not null)
                await DeleteRemoteDeviceAsync(identity, SignOutDeleteBudget, cancellationToken).ConfigureAwait(false);

            await ForgetDeviceAsync(cancellationToken).ConfigureAwait(false);
            _signInRejected = false;
            Publish(PushRelayAvailability.SignInRequired, _stateStore.Load(), null);
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

    public async Task<PushRelayPushTestResult> SendPushTestAsync(CancellationToken cancellationToken = default)
    {
        if (!IsPlatformSupported || _identityStore is not { } identityStore)
            return PushRelayPushTestResult.NotRegistered;

        var session = await _sessions.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var identity = await identityStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (session is null || identity is null || identity.WinoUserId != session.AccountId
            || Snapshot.Availability != PushRelayAvailability.Enabled)
        {
            return PushRelayPushTestResult.NotRegistered;
        }

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

            entry.Status = status ?? entry.Status;
            entry.MonitoringReason = reason;
            entry.MonitoringMessage = message;
            entry.UpdatedUtc = UtcNow;

            if (status != PushRelayMailboxStatuses.NeedsReauth)
                return false;

            // The relay can no longer sign in: upload the current credentials again on the next pass.
            entry.CredentialFingerprint = null;
            entry.LastErrorCode = null;
            return true;
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
        RequestSync(PushRelaySyncReason.WinoAccountChanged);
    }

    public void Receive(WinoAccountSignedOutMessage message) => RequestSync(PushRelaySyncReason.WinoAccountChanged);

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
        if (!IsPlatformSupported || _identityStore is not { } identityStore || _deviceInfo is not { } deviceInfo)
        {
            Publish(PushRelayAvailability.UnsupportedPlatform, state, null);
            return;
        }

        var session = await _sessions.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var identity = await identityStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var gate = EvaluateGate(state, session is not null);

        if (gate != PushRelayAvailability.Enabled || session is null)
        {
            if (identity is not null && gate is PushRelayAvailability.SignInRequired or PushRelayAvailability.DisabledByUser)
            {
                _logger.Information("Live notifications are {Availability}; removing this relay device.", gate);
                if (session is not null)
                    await DeleteRemoteDeviceAsync(identity, OptOutDeleteBudget, cancellationToken).ConfigureAwait(false);

                await ForgetDeviceAsync(cancellationToken).ConfigureAwait(false);
                state = _stateStore.Load();
            }

            Publish(gate, state, null);
            return;
        }

        var now = UtcNow;
        var forced = reasons.Overlaps(ForcingReasons);
        if (!forced && state.NextAttemptUtc is { } next && next > now)
        {
            Publish(ComputeAvailability(state, true), state, identity);
            return;
        }

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
        Publish(ComputeAvailability(state, true), state, identity);
    }

    private async Task<PushRelayDeviceIdentity?> RunEnabledPassAsync(PushRelayState state, WinoAccountSession session,
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

                if (identity is null || identity.WinoUserId != session.AccountId)
                {
                    if (identity is not null)
                    {
                        // Registered for another Wino Account: that device is not ours to delete anymore.
                        _logger.Information("The signed-in Wino Account changed; registering a new relay device.");
                        await identityStore.DeleteAsync(cancellationToken).ConfigureAwait(false);
                        identity = null;
                    }

                    state.Mailboxes.Clear();
                    PushRelayRegisterDeviceResponse registration;
                    try
                    {
                        registration = await _client.RegisterDeviceAsync(
                            new PushRelayRegisterDeviceRequest(descriptor.Platform, pushToken, appVersion, appBuild, locale, osVersion, name, socketKey),
                            cancellationToken).ConfigureAwait(false);
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

                    identity = new PushRelayDeviceIdentity(registration.DeviceId, session.AccountId, descriptor.Platform, now);
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

                limitReached = await CreateMailboxAsync(identity, entry, registration, now, cancellationToken).ConfigureAwait(false);
            }
            else if (entry.CredentialFingerprint != registration.CredentialFingerprint || failedWithSameCredentials
                || entry.Status == PushRelayMailboxStatuses.NeedsReauth)
            {
                limitReached |= await UpdateCredentialsAsync(identity, entry, registration, now, allowCreate: true, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Returns true when the relay reported the mailbox limit.</summary>
    private async Task<bool> CreateMailboxAsync(PushRelayDeviceIdentity identity, PushRelayMailboxRegistration entry,
        PushRelayMailboxRegistrationRequest registration, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            var mailbox = await _client.CreateMailboxAsync(identity.DeviceId, registration.Create, cancellationToken).ConfigureAwait(false);
            Succeeded(entry, mailbox, registration, now);
            return false;
        }
        catch (PushRelayApiException ex) when (ex.Code == PushRelayErrorCodes.MailboxAlreadyAdded && ex.Problem.SubscriptionId is { } subscriptionId)
        {
            entry.SubscriptionId = subscriptionId;
            return await UpdateCredentialsAsync(identity, entry, registration, now, allowCreate: false, cancellationToken).ConfigureAwait(false);
        }
        catch (PushRelayApiException ex) when (IsMailboxScoped(ex))
        {
            Failed(entry, ex, registration, now);
            return ex.Code == PushRelayErrorCodes.MailboxLimitReached;
        }
    }

    private async Task<bool> UpdateCredentialsAsync(PushRelayDeviceIdentity identity, PushRelayMailboxRegistration entry,
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
            return await CreateMailboxAsync(identity, entry, registration, now, cancellationToken).ConfigureAwait(false);
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
        if (ex.IsSignInRequired || ex.IsTokenRejected)
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

    private PushRelayAvailability EvaluateGate(PushRelayState state, bool hasSession)
    {
        if (!IsPlatformSupported)
            return PushRelayAvailability.UnsupportedPlatform;
        if (!hasSession)
            return PushRelayAvailability.SignInRequired;
        if (IsOptInPlatform && !state.UserOptIn)
            return PushRelayAvailability.DisabledByUser;
        if (state.PlatformRejected && state.PlatformRejectedAppVersion == Normalize(_metadata.AppVersion))
            return PushRelayAvailability.RejectedByRelay;
        return PushRelayAvailability.Enabled;
    }

    private PushRelayAvailability ComputeAvailability(PushRelayState state, bool hasSession)
    {
        var gate = EvaluateGate(state, hasSession);
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
            state.UserOptIn,
            deviceId,
            CurrentPushToken is not null,
            state.LastCheckInUtc,
            state.NextAttemptUtc,
            state.LastErrorCode,
            state.LastErrorDetail,
            mailboxes,
            state.MailboxLimit ?? _tokens.MailboxLimit ?? PushRelayDefaults.DefaultMailboxLimit,
            state.LastTestPushUtc);

        Volatile.Write(ref _snapshot, snapshot);
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

    private string? CurrentPushToken
        => _pushTokens is { IsPushChannelAvailable: true, CurrentToken: { Length: > 0 } token } ? token : null;

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
