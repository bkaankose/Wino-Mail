#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;
using Wino.Core.Domain.Models.Settings;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Server;
using Wino.Messaging.UI;
using Wino.Services.Logging;

namespace Wino.Services.Synchronization;

/// <summary>
/// Owns the decisions that used to be spread over the runtime, the relay registration, the socket coordinator and the
/// IMAP synchronizers: the effective sync mode, per-account live coverage, the timer intervals, bounded catch-ups when
/// the socket drops or returns, and when a stalled relay deserves another try. State is one immutable snapshot swapped
/// under a private lock; events are raised outside it. Every decision is logged once, on change, never per tick.
/// </summary>
public sealed class SyncCoordinator : ISyncCoordinator,
    IRecipient<AccountCreatedMessage>, IRecipient<AccountRemovedMessage>, IRecipient<AccountUpdatedMessage>,
    IRecipient<ImapIdleStateChanged>
{
    internal static readonly TimeSpan SocketDownGrace = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan SocketUpStable = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan FlapWindow = TimeSpan.FromMinutes(10);
    internal const int FlapThreshold = 6;
    internal static readonly TimeSpan FlapCooldown = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan RetryThrottle = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan NetworkDebounce = TimeSpan.FromSeconds(5);

    private static readonly (string Name, Func<IPreferencesService, object?> Read)[] WatchedPreferences =
    [
        (nameof(IPreferencesService.SyncMethod), p => p.SyncMethod),
        (nameof(IPreferencesService.EmailSyncIntervalMinutes), p => p.EmailSyncIntervalMinutes),
        (nameof(IPreferencesService.CalendarSyncIntervalMinutes), p => p.CalendarSyncIntervalMinutes),
        (nameof(IPreferencesService.AreQuietHoursEnabled), p => p.AreQuietHoursEnabled),
        (nameof(IPreferencesService.QuietHoursStart), p => p.QuietHoursStart),
        (nameof(IPreferencesService.QuietHoursEnd), p => p.QuietHoursEnd),
        (nameof(IPreferencesService.QuietHoursDays), p => p.QuietHoursDays),
        (nameof(IPreferencesService.NotificationSnoozePreset), p => p.NotificationSnoozePreset),
        (nameof(IPreferencesService.NotificationSnoozeUntilUtcTicks), p => p.NotificationSnoozeUntilUtcTicks == 0 ? null : new DateTimeOffset(p.NotificationSnoozeUntilUtcTicks, TimeSpan.Zero)),
        (nameof(IPreferencesService.SnoozeWhilePresenting), p => p.SnoozeWhilePresenting),
        (nameof(IPreferencesService.AreNewMailNotificationsEnabled), p => p.AreNewMailNotificationsEnabled),
        (nameof(IPreferencesService.MailNotificationScope), p => p.MailNotificationScope),
        (nameof(IPreferencesService.MailNotificationContent), p => p.MailNotificationContent),
        (nameof(IPreferencesService.AreCalendarRemindersEnabled), p => p.AreCalendarRemindersEnabled),
        (nameof(IPreferencesService.AreTaskRemindersEnabled), p => p.AreTaskRemindersEnabled),
    ];

    private readonly IPreferencesService _preferences;
    private readonly IPlatformCapabilities _capabilities;
    private readonly IAccountService _accountService;
    private readonly IRelayIntroductionService? _introduction;
    private readonly IPushRelayRegistrationService _registration;
    private readonly IRelaySocketCoordinator? _socket;
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly IMessenger _messenger;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger = Log.ForContext<SyncCoordinator>();
    private readonly ThrottledLog _throttle = new(TimeSpan.FromMinutes(10));

    private readonly object _gate = new();
    private readonly Dictionary<Guid, AccountFacts> _accounts = new();
    private readonly Dictionary<Guid, ImapIdleStatus> _idle = new();
    private readonly Dictionary<string, string> _preferenceValues = new(StringComparer.Ordinal);
    private readonly Dictionary<RelayRetryTrigger, DateTimeOffset> _lastRetry = new();
    private readonly Queue<DateTimeOffset> _socketTransitions = new();

    private SyncCoordinationSnapshot _snapshot;
    private PushRelayRegistrationSnapshot? _relay;
    private bool _rawSocketConnected;
    private bool _stableSocketConnected;
    private bool _flapDownRecorded;
    private DateTimeOffset? _socketDownSinceUtc;
    private DateTimeOffset? _cooldownUntilUtc;
    private DateTimeOffset? _lastSuccessfulSyncUtc;
    private bool _networkAvailable = true;
    private ITimer? _graceTimer;
    private ITimer? _stableTimer;
    private ITimer? _cooldownTimer;
    private ITimer? _networkTimer;
    private ITimer? _connectGraceTimer;
    private volatile bool _connectGraceActive;
    private bool _initialized;
    private bool _disposed;

    public SyncCoordinator(
        IPreferencesService preferences,
        IPlatformCapabilities capabilities,
        IAccountService accountService,
        IPushRelayRegistrationService registration,
        ISynchronizationManager synchronizationManager,
        IMessenger messenger,
        IRelayIntroductionService? introduction = null,
        IRelaySocketCoordinator? socket = null,
        TimeProvider? timeProvider = null)
    {
        _preferences = preferences;
        _capabilities = capabilities;
        _accountService = accountService;
        _registration = registration;
        _synchronizationManager = synchronizationManager;
        _messenger = messenger;
        _introduction = introduction;
        _socket = socket;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _snapshot = SyncCoordinationSnapshot.Initial(HasChoice ? EffectiveSyncMode.RelayFallback : EffectiveSyncMode.PlatformPush, _timeProvider.GetUtcNow());
    }

    /// <summary>Desktop: the user picks timer-based or relay-based sync. Mobile: pushes only.</summary>
    private bool HasChoice => SettingAvailability.IsAvailable(PlatformSetting.SyncMethod, _capabilities);

    private bool HasIntervals => SettingAvailability.IsAvailable(PlatformSetting.SyncIntervals, _capabilities);

    public SyncCoordinationSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public bool IsSocketConnected => Snapshot.IsSocketConnected;

    /// <summary>
    /// The shell's red dot: relay-based sync is selected and the connection failed. It follows the raw socket, not the
    /// sync mode, so a connected socket clears it at once although the mode waits <see cref="SocketUpStable"/> before
    /// it skips timer ticks. The connect grace (after launch, or after the user turns relay-based sync on) and a drop
    /// within the socket-down grace are not failures yet. A flap cool-down counts as failed even while connected.
    /// </summary>
    public bool IsFallbackActive
    {
        get
        {
            var snapshot = Snapshot;
            return snapshot.Mode == EffectiveSyncMode.RelayFallback
                && (snapshot.IsFlapCooldownActive || (!snapshot.IsSocketConnected && !_connectGraceActive));
        }
    }

    public DateTimeOffset? LastChangeUtc => Snapshot.ChangedUtc;

    public event EventHandler? Changed;

    public event EventHandler<SyncCatchUpReason>? CatchUpRequested;

    public TimeSpan MailSyncInterval
        => TimeSpan.FromMinutes(Math.Max(1, HasIntervals ? _preferences.EmailSyncIntervalMinutes : Constants.DefaultEmailSyncIntervalMinutes));

    public TimeSpan CalendarSyncInterval
        => TimeSpan.FromMinutes(Math.Max(1, HasIntervals ? _preferences.CalendarSyncIntervalMinutes : Constants.DefaultCalendarSyncIntervalMinutes));

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_initialized || _disposed)
                return;
            _initialized = true;
        }

        // Upgraded installs keep the timer until the user chooses; fresh installs and mobile start on the relay.
        // Decided here, before any synchronizer or loop reads the preference. A failure must never fail startup:
        // the preference then stays as it is and the shell's own introduction check retries the decision.
        if (_introduction is not null)
        {
            try { await _introduction.IsIntroductionPendingAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.Warning(ex, "The sync method decision failed; keeping the stored preference."); }
        }

        List<MailAccount> accounts;
        try { accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "The sync coordinator could not read the accounts at startup; coverage starts empty.");
            accounts = [];
        }

        lock (_gate)
        {
            // A relay-mode launch starts in fallback until the socket is stable; the red dot stays hidden for one grace
            // period so a normal launch never shows "offline". The mode itself is not affected.
            if (HasChoice && _socket is not null && _preferences.SyncMethod == SyncMethod.Relay)
                StartConnectGrace();

            _accounts.Clear();
            foreach (var account in accounts)
                _accounts[account.Id] = AccountFacts.From(account);

            foreach (var synchronizer in EnumerateSynchronizers())
            {
                if (synchronizer is IImapSynchronizer imap && synchronizer.Account is { } account)
                    _idle[account.Id] = imap.IdleStatus;
            }

            _relay = _registration.Snapshot;
            if (_socket is not null)
            {
                _rawSocketConnected = _socket.IsSocketConnected;
                _stableSocketConnected = _rawSocketConnected;
            }

            foreach (var (name, read) in WatchedPreferences)
                _preferenceValues[name] = Describe(read(_preferences));
        }

        _preferences.PreferenceChanged += PreferenceChanged;
        _registration.SnapshotChanged += RegistrationSnapshotChanged;
        if (_socket is not null)
            _socket.Changed += SocketChanged;

        _messenger.Register<AccountCreatedMessage>(this);
        _messenger.Register<AccountRemovedMessage>(this);
        _messenger.Register<AccountUpdatedMessage>(this);
        _messenger.Register<ImapIdleStateChanged>(this);

        if (HasChoice)
        {
            try
            {
                _networkAvailable = NetworkInterface.GetIsNetworkAvailable();
                NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Network availability events are not available; relay retries rely on foreground and settings.");
            }
        }

        Recompute("startup", logCoverage: false);
        LogStartupSnapshot(accounts);
    }

    /// <summary>
    /// True when the timer must synchronize the account: it has no live coverage and nothing blocks it. An account that
    /// needs attention is never synchronized by the timer, in any mode, until the user fixes it.
    /// </summary>
    public bool ShouldRunTimerSync(Guid accountId)
    {
        if (!Snapshot.Coverage.TryGetValue(accountId, out var coverage))
            return true;

        return !coverage.IsCovered && coverage.Gap != LiveCoverageGap.AccountAttention;
    }

    public void ReportTimerMailSyncCompleted(Guid accountId, bool succeeded, DateTimeOffset completedUtc)
    {
        if (!succeeded)
            return;

        lock (_gate)
        {
            if (_lastSuccessfulSyncUtc is null || completedUtc > _lastSuccessfulSyncUtc)
                _lastSuccessfulSyncUtc = completedUtc;
        }
    }

    public void RequestRelayRetry(RelayRetryTrigger trigger)
    {
        if (!HasChoice)
            return;

        var snapshot = Snapshot;
        if (snapshot.Mode == EffectiveSyncMode.Timer)
            return;

        var relay = _relay ?? _registration.Snapshot;
        var stalled = snapshot.Mode == EffectiveSyncMode.RelayFallback
            || relay.Availability != PushRelayAvailability.Enabled
            || relay.NextAttemptUtc is not null;

        if (!stalled)
        {
            _logger.Debug("Relay retry on {Trigger} not needed: live updates are healthy.", trigger);
            return;
        }

        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_lastRetry.TryGetValue(trigger, out var last) && now - last < RetryThrottle)
            {
                _logger.Information("Relay retry on {Trigger} throttled; the previous one ran {Ago:F0} s ago.", trigger, (now - last).TotalSeconds);
                return;
            }

            _lastRetry[trigger] = now;
        }

        _logger.Information("Relay retry on {Trigger}: mode {Mode}, availability {Availability}, next scheduled attempt {NextAttemptUtc:O}.",
            trigger, snapshot.Mode, relay.Availability, relay.NextAttemptUtc);

        switch (trigger)
        {
            case RelayRetryTrigger.NetworkRestored:
                _registration.RequestSync(PushRelaySyncReason.UserRequested);
                if (_socket is { } socket)
                    _ = ReconnectSocketAsync(socket);
                break;
            case RelayRetryTrigger.SettingsOpened:
                _registration.RequestSync(PushRelaySyncReason.UserRequested);
                break;
            default:
                _registration.RequestSync(PushRelaySyncReason.Foreground);
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            DisposeTimer(ref _graceTimer);
            DisposeTimer(ref _stableTimer);
            DisposeTimer(ref _cooldownTimer);
            DisposeTimer(ref _networkTimer);
            DisposeTimer(ref _connectGraceTimer);
        }

        if (!_initialized)
            return;

        _preferences.PreferenceChanged -= PreferenceChanged;
        _registration.SnapshotChanged -= RegistrationSnapshotChanged;
        if (_socket is not null)
            _socket.Changed -= SocketChanged;
        _messenger.UnregisterAll(this);

        if (HasChoice)
        {
            try { NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged; }
            catch (Exception ex) { _logger.Debug(ex, "Network availability events could not be unsubscribed."); }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    #region Inputs

    public void Receive(AccountCreatedMessage message)
    {
        lock (_gate) _accounts[message.Account.Id] = AccountFacts.From(message.Account);
        Recompute($"account {message.Account.Name} added");
    }

    public void Receive(AccountRemovedMessage message)
    {
        lock (_gate)
        {
            _accounts.Remove(message.Account.Id);
            _idle.Remove(message.Account.Id);
        }

        Recompute($"account {message.Account.Name} removed");
    }

    public void Receive(AccountUpdatedMessage message)
    {
        var updated = AccountFacts.From(message.Account);
        AccountFacts? previous;
        lock (_gate)
        {
            _accounts.TryGetValue(message.Account.Id, out previous);
            _accounts[message.Account.Id] = updated;
        }

        if (previous is null)
        {
            Recompute($"account {message.Account.Name} updated");
            return;
        }

        var changes = previous.DescribeNotificationChanges(updated);
        if (changes.Count > 0)
            _logger.Information("Notification settings changed for account {AccountName} ({AccountId}): {Changes}.", message.Account.Name, message.Account.Id, string.Join(", ", changes));

        if (previous.IsNetworkBlocked != updated.IsNetworkBlocked || previous.IsMailAccessGranted != updated.IsMailAccessGranted)
            Recompute($"account {message.Account.Name} attention changed");
    }

    public void Receive(ImapIdleStateChanged message)
    {
        lock (_gate) _idle[message.AccountId] = message.Status;
        Recompute($"IMAP IDLE {message.Status.State} ({message.Status.Reason})");
    }

    private void PreferenceChanged(object? sender, string propertyName)
    {
        var watched = Array.Find(WatchedPreferences, p => p.Name == propertyName);
        if (watched.Read is null)
            return;

        string current;
        string? previous;
        try { current = Describe(watched.Read(_preferences)); }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Preference {Preference} could not be read for logging.", propertyName);
            return;
        }

        lock (_gate)
        {
            _preferenceValues.TryGetValue(propertyName, out previous);
            if (previous == current)
                return;
            _preferenceValues[propertyName] = current;
        }

        _logger.Information("Preference {Preference} changed {Previous} -> {Current}.", propertyName, previous ?? "(unset)", current);

        if (propertyName == nameof(IPreferencesService.SyncMethod))
        {
            lock (_gate)
            {
                if (_preferences.SyncMethod == SyncMethod.Relay)
                {
                    if (HasChoice && _socket is not null)
                        StartConnectGrace();
                }
                else
                {
                    // Turning relay-based sync off stops the socket on purpose. The socket must prove itself stable
                    // again when it is turned back on, or the mode would claim live coverage before it reconnects.
                    _stableSocketConnected = false;
                    DisposeTimer(ref _stableTimer);
                    DisposeTimer(ref _graceTimer);
                }
            }
        }

        if (propertyName is nameof(IPreferencesService.SyncMethod) or nameof(IPreferencesService.EmailSyncIntervalMinutes) or nameof(IPreferencesService.CalendarSyncIntervalMinutes))
            Recompute($"preference {propertyName}");
    }

    private void RegistrationSnapshotChanged(object? sender, PushRelayRegistrationSnapshot snapshot)
    {
        lock (_gate) _relay = snapshot;
        Recompute("relay registration");
    }

    private void SocketChanged(object? sender, EventArgs e)
    {
        if (_socket is not { } socket)
            return;

        var connected = socket.IsSocketConnected;
        var outage = socket.LastOutage;
        var now = _timeProvider.GetUtcNow();
        string? cause = null;
        SyncCatchUpReason? catchUp = null;
        string? catchUpNote = null;

        lock (_gate)
        {
            if (_disposed)
                return;

            if (connected && !_rawSocketConnected)
            {
                _rawSocketConnected = true;
                // Only the return from a counted outage is a flap; a start after a stop or an owner handover is not.
                if (_flapDownRecorded)
                    RecordTransition(now);
                _flapDownRecorded = false;
                DisposeTimer(ref _graceTimer);

                // Back within the grace period: the mode never left Relay, so no transition will catch up for the gap.
                if (_stableSocketConnected && _snapshot.Mode == EffectiveSyncMode.Relay && _socketDownSinceUtc is not null)
                {
                    (catchUp, catchUpNote) = DecideRecoveryCatchUp(now);
                    _socketDownSinceUtc = null;
                }
                // Stays "down" until the socket survives the stable window; a bounce never flips the mode.
                if (!_stableSocketConnected)
                    _stableTimer = StartTimer(ref _stableTimer, SocketUpStable, SocketStableElapsed);
            }
            else if (!connected && _rawSocketConnected)
            {
                _rawSocketConnected = false;
                // Turning relay-based sync off closes the socket too; that is no outage to catch up after.
                if (_preferences.SyncMethod == SyncMethod.Relay)
                    _socketDownSinceUtc ??= now;
                // A stop the user asked for and the relay's owner handover close the socket on purpose (outage None).
                _flapDownRecorded = outage != RelaySocketOutageKind.None && _preferences.SyncMethod == SyncMethod.Relay;
                if (_flapDownRecorded)
                    RecordTransition(now);
                DisposeTimer(ref _stableTimer);

                if (outage == RelaySocketOutageKind.Terminal)
                {
                    _stableSocketConnected = false;
                    cause = $"socket down ({outage})";
                }
                else if (_stableSocketConnected)
                {
                    _graceTimer = StartTimer(ref _graceTimer, SocketDownGrace, SocketGraceElapsed);
                }
            }
            else if (!connected && outage == RelaySocketOutageKind.Terminal && _stableSocketConnected)
            {
                // A retrying outage turned terminal while the grace period was still running.
                DisposeTimer(ref _graceTimer);
                _stableSocketConnected = false;
                cause = $"socket down ({outage})";
            }

            cause ??= DetectFlap(now);
        }

        if (cause is not null)
            Recompute(cause);
        else
            RaiseChangedIfSocketStateDiffers();

        if (catchUpNote is not null)
            _logger.Information("Catch-up on relay socket reconnect: {Note}.", catchUpNote);

        if (catchUp is { } reason)
        {
            try { CatchUpRequested?.Invoke(this, reason); }
            catch (Exception ex) { _logger.Warning(ex, "A catch-up subscriber threw."); }
        }
    }

    private void SocketGraceElapsed(object? state)
    {
        lock (_gate)
        {
            DisposeTimer(ref _graceTimer);
            if (_disposed || _rawSocketConnected || !_stableSocketConnected)
                return;
            _stableSocketConnected = false;
        }

        Recompute($"socket down for {SocketDownGrace.TotalSeconds:F0} s");
    }

    private void SocketStableElapsed(object? state)
    {
        lock (_gate)
        {
            DisposeTimer(ref _stableTimer);
            if (_disposed || !_rawSocketConnected || _stableSocketConnected)
                return;
            _stableSocketConnected = true;
        }

        Recompute($"socket up for {SocketUpStable.TotalSeconds:F0} s");
    }

    /// <summary>Called under <see cref="_gate"/>.</summary>
    private void StartConnectGrace()
    {
        _connectGraceActive = true;
        _connectGraceTimer = StartTimer(ref _connectGraceTimer, SocketDownGrace, ConnectGraceElapsed);
    }

    private void ConnectGraceElapsed(object? state)
    {
        lock (_gate)
        {
            DisposeTimer(ref _connectGraceTimer);
            if (_disposed || !_connectGraceActive)
                return;
            _connectGraceActive = false;
        }

        if (Snapshot.Mode == EffectiveSyncMode.RelayFallback)
            _logger.Information("Live updates did not come up within {Seconds:F0} s; fallback indicator shown={Shown}.", SocketDownGrace.TotalSeconds, IsFallbackActive);

        RaiseChanged();
    }

    private void CooldownElapsed(object? state)
    {
        lock (_gate)
        {
            DisposeTimer(ref _cooldownTimer);
            if (_disposed)
                return;
            _cooldownUntilUtc = null;
            _socketTransitions.Clear();
        }

        Recompute("flap cool-down ended");
    }

    /// <summary>Called under <see cref="_gate"/>. Returns a cause when a cool-down starts.</summary>
    private string? DetectFlap(DateTimeOffset now)
    {
        if (_cooldownUntilUtc is not null)
            return null;

        while (_socketTransitions.Count > 0 && now - _socketTransitions.Peek() > FlapWindow)
            _socketTransitions.Dequeue();

        if (_socketTransitions.Count < FlapThreshold)
            return null;

        _cooldownUntilUtc = now + FlapCooldown;
        _cooldownTimer = StartTimer(ref _cooldownTimer, FlapCooldown, CooldownElapsed);
        return $"socket flapped {_socketTransitions.Count} times in {FlapWindow.TotalMinutes:F0} min";
    }

    private void RecordTransition(DateTimeOffset now) => _socketTransitions.Enqueue(now);

    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        bool restored;
        lock (_gate)
        {
            if (_disposed || _networkAvailable == e.IsAvailable)
                return;
            _networkAvailable = e.IsAvailable;
            restored = e.IsAvailable;
            DisposeTimer(ref _networkTimer);
            if (restored)
                _networkTimer = StartTimer(ref _networkTimer, NetworkDebounce, NetworkRestoredElapsed);
        }

        _logger.Information("Network availability changed: {Available}.", e.IsAvailable ? "available" : "unavailable");
    }

    private void NetworkRestoredElapsed(object? state)
    {
        lock (_gate)
        {
            DisposeTimer(ref _networkTimer);
            if (_disposed || !_networkAvailable)
                return;
        }

        RequestRelayRetry(RelayRetryTrigger.NetworkRestored);
    }

    private async Task ReconnectSocketAsync(IRelaySocketCoordinator socket)
    {
        try { await socket.RequestReconnectAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.Warning(ex, "The relay socket reconnect request failed."); }
    }

    #endregion

    #region Decisions

    /// <summary>Rebuilds the snapshot from the inputs, logs what changed and raises events outside the lock.</summary>
    private void Recompute(string cause, bool logCoverage = true)
    {
        SyncCoordinationSnapshot previous;
        SyncCoordinationSnapshot next;
        SyncCatchUpReason? catchUp = null;
        string? catchUpNote = null;
        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            if (_disposed)
                return;

            previous = _snapshot;
            var mode = ComputeMode();
            var coverage = ComputeCoverage(mode, previous.Coverage, now);
            var changed = mode != previous.Mode
                || _rawSocketConnected != previous.IsSocketConnected
                || (_cooldownUntilUtc is not null) != previous.IsFlapCooldownActive
                || !SameCoverage(previous.Coverage, coverage);

            next = changed
                ? new SyncCoordinationSnapshot(mode, _rawSocketConnected, _cooldownUntilUtc is not null, now, coverage)
                : previous;

            if (mode != previous.Mode)
                (catchUp, catchUpNote) = DecideCatchUp(previous.Mode, mode, now);

            // Connected again, or the user left relay mode (a stop the user asked for is no outage).
            if ((_rawSocketConnected && mode == EffectiveSyncMode.Relay) || mode == EffectiveSyncMode.Timer)
                _socketDownSinceUtc = null;

            // Live updates are up, or the user left relay mode: the connect grace has nothing left to hide.
            if (mode != EffectiveSyncMode.RelayFallback && _connectGraceActive)
            {
                _connectGraceActive = false;
                DisposeTimer(ref _connectGraceTimer);
            }

            Volatile.Write(ref _snapshot, next);
        }

        if (ReferenceEquals(next, previous))
            return;

        if (next.Mode != previous.Mode)
        {
            _logger.Information("Sync mode {Previous} -> {Current} because {Cause}; socket connected={SocketConnected}, flap cool-down={Cooldown}, mail interval {MailInterval} min, calendar interval {CalendarInterval} min.",
                previous.Mode, next.Mode, cause, next.IsSocketConnected, next.IsFlapCooldownActive, MailSyncInterval.TotalMinutes, CalendarSyncInterval.TotalMinutes);
        }
        else if (next.IsFlapCooldownActive != previous.IsFlapCooldownActive)
        {
            _logger.Information("Relay socket flap cool-down {State} because {Cause}.", next.IsFlapCooldownActive ? "started" : "ended", cause);
        }

        if (logCoverage)
            LogCoverageChanges(previous.Coverage, next.Coverage, cause);

        if (catchUpNote is not null)
            _logger.Information("Catch-up on {Transition}: {Note}.", $"{previous.Mode}->{next.Mode}", catchUpNote);

        RaiseChanged();

        if (catchUp is { } reason)
        {
            try { CatchUpRequested?.Invoke(this, reason); }
            catch (Exception ex) { _logger.Warning(ex, "A catch-up subscriber threw."); }
        }
    }

    /// <summary>Called under <see cref="_gate"/>.</summary>
    private EffectiveSyncMode ComputeMode()
    {
        if (!HasChoice)
            return EffectiveSyncMode.PlatformPush;
        if (_preferences.SyncMethod == SyncMethod.Timer)
            return EffectiveSyncMode.Timer;
        if (_socket is null)
            return EffectiveSyncMode.RelayFallback;
        if (_cooldownUntilUtc is not null || !_stableSocketConnected)
            return EffectiveSyncMode.RelayFallback;
        if (_relay is { } relay && (relay.Availability != PushRelayAvailability.Enabled || relay.DeviceId is null))
            return EffectiveSyncMode.RelayFallback;
        return EffectiveSyncMode.Relay;
    }

    /// <summary>Called under <see cref="_gate"/>.</summary>
    private Dictionary<Guid, AccountLiveCoverage> ComputeCoverage(EffectiveSyncMode mode, IReadOnlyDictionary<Guid, AccountLiveCoverage> previous, DateTimeOffset now)
    {
        var result = new Dictionary<Guid, AccountLiveCoverage>(_accounts.Count);
        foreach (var (id, facts) in _accounts)
        {
            var (kind, gap) = ComputeAccountCoverage(mode, id, facts);
            var since = previous.TryGetValue(id, out var old) && old.Kind == kind && old.Gap == gap ? old.SinceUtc : now;
            result[id] = new AccountLiveCoverage(id, kind, gap, since);
        }

        return result;
    }

    private (LiveCoverageKind Kind, LiveCoverageGap Gap) ComputeAccountCoverage(EffectiveSyncMode mode, Guid accountId, AccountFacts facts)
    {
        // Attention comes first, in every mode: such an account is neither covered live nor synchronized by the timer.
        if (facts.IsNetworkBlocked || !facts.IsMailAccessGranted)
            return (LiveCoverageKind.None, LiveCoverageGap.AccountAttention);

        switch (mode)
        {
            case EffectiveSyncMode.PlatformPush:
                return (LiveCoverageKind.None, LiveCoverageGap.PlatformPush);
            case EffectiveSyncMode.Timer:
                return (LiveCoverageKind.None, LiveCoverageGap.TimerMode);
            case EffectiveSyncMode.RelayFallback:
                return (LiveCoverageKind.None, _cooldownUntilUtc is not null ? LiveCoverageGap.FlapCooldown : LiveCoverageGap.SocketDown);
        }

        switch (facts.Provider)
        {
            case MailProviderType.Gmail:
            case MailProviderType.Outlook:
                var mailbox = _relay?.Mailboxes.FirstOrDefault(m => m.AccountId == accountId);
                if (mailbox is null || !mailbox.IsWatched)
                    return (LiveCoverageKind.None, LiveCoverageGap.MailboxNotWatched);
                if (mailbox.NeedsReauthentication)
                    return (LiveCoverageKind.None, LiveCoverageGap.MailboxNeedsReauth);
                if (mailbox.Status == PushRelayMailboxStatuses.Error || mailbox.LastErrorCode is not null)
                    return (LiveCoverageKind.None, LiveCoverageGap.MailboxError);
                return (LiveCoverageKind.RelayMailbox, LiveCoverageGap.None);

            case MailProviderType.IMAP4:
                if (!_idle.TryGetValue(accountId, out var idle))
                    return (LiveCoverageKind.None, LiveCoverageGap.IdleNotRunning);
                if (idle.State == ImapIdleState.Listening)
                    return (LiveCoverageKind.ImapIdle, LiveCoverageGap.None);
                return (LiveCoverageKind.None, IsIdleUnsupportedReason(idle.Reason) ? LiveCoverageGap.IdleUnsupported : LiveCoverageGap.IdleNotRunning);

            default:
                return (LiveCoverageKind.None, LiveCoverageGap.ProviderUnsupported);
        }
    }

    private static bool IsIdleUnsupportedReason(string? reason)
        => reason is not null && reason.Contains("IDLE", StringComparison.OrdinalIgnoreCase)
           && (reason.Contains("capability", StringComparison.OrdinalIgnoreCase)
               || reason.Contains("rejected", StringComparison.OrdinalIgnoreCase)
               || reason.Contains("cannot", StringComparison.OrdinalIgnoreCase));

    /// <summary>Called under <see cref="_gate"/>. Returns the catch-up to raise, if any, and a note for the log.</summary>
    private (SyncCatchUpReason? Reason, string? Note) DecideCatchUp(EffectiveSyncMode previous, EffectiveSyncMode current, DateTimeOffset now)
    {
        // Covered accounts were skipping their ticks; the user chose the timer, so they sync now instead of waiting
        // up to one interval (the loops used to be restarted for this, which ran an immediate pass).
        if (previous == EffectiveSyncMode.Relay && current == EffectiveSyncMode.Timer)
            return (SyncCatchUpReason.SyncMethodChanged, "requested, accounts covered by the relay resume timer-based sync");

        if (previous == EffectiveSyncMode.Relay && current == EffectiveSyncMode.RelayFallback)
        {
            var interval = MailSyncInterval;
            if (_lastSuccessfulSyncUtc is { } last && now - last < interval)
                return (null, $"skipped, the last automatic sync finished {(now - last).TotalSeconds:F0} s ago (interval {interval.TotalMinutes:F0} min)");

            return (SyncCatchUpReason.FallbackEntered, _lastSuccessfulSyncUtc is null
                ? "requested, no automatic sync has completed yet"
                : $"requested, the last automatic sync is {(now - _lastSuccessfulSyncUtc.Value).TotalMinutes:F1} min old");
        }

        if (previous == EffectiveSyncMode.RelayFallback && current == EffectiveSyncMode.Relay)
            return DecideRecoveryCatchUp(now);

        return (null, null);
    }

    /// <summary>
    /// Called under <see cref="_gate"/>. The relay holds changes for a desktop only for a short grace after its socket closes, so
    /// every reconnect after a real outage catches up once, relay-covered accounts included. The flap cool-down bounds how often
    /// that can happen.
    /// </summary>
    private (SyncCatchUpReason? Reason, string? Note) DecideRecoveryCatchUp(DateTimeOffset now)
    {
        if (_socketDownSinceUtc is not { } downSince)
            return (null, "skipped, no outage was recorded (first connection)");

        return (SyncCatchUpReason.Recovered, $"requested after a {(now - downSince).TotalSeconds:F0} s outage");
    }

    private static bool SameCoverage(IReadOnlyDictionary<Guid, AccountLiveCoverage> left, IReadOnlyDictionary<Guid, AccountLiveCoverage> right)
    {
        if (left.Count != right.Count)
            return false;

        foreach (var (id, coverage) in left)
        {
            if (!right.TryGetValue(id, out var other) || other.Kind != coverage.Kind || other.Gap != coverage.Gap)
                return false;
        }

        return true;
    }

    private void LogCoverageChanges(IReadOnlyDictionary<Guid, AccountLiveCoverage> previous, IReadOnlyDictionary<Guid, AccountLiveCoverage> current, string cause)
    {
        foreach (var (id, coverage) in current)
        {
            if (previous.TryGetValue(id, out var old) && old.Kind == coverage.Kind && old.Gap == coverage.Gap)
                continue;

            string name;
            lock (_gate) name = _accounts.TryGetValue(id, out var facts) ? facts.Name : id.ToString();

            if (coverage.IsCovered)
                _logger.Information("Account {AccountName} is covered live by {CoverageKind}; timer ticks are skipped ({Cause}).", name, coverage.Kind, cause);
            else if (coverage.Gap == LiveCoverageGap.AccountAttention)
                _logger.Information("Account {AccountName} needs attention; automatic synchronization is paused until it is fixed ({Cause}).", name, cause);
            else
                _logger.Information("Account {AccountName} syncs on the timer: {CoverageGap} ({Cause}).", name, coverage.Gap, cause);
        }
    }

    private void LogStartupSnapshot(IReadOnlyList<MailAccount> accounts)
    {
        var snapshot = Snapshot;
        var quietHours = _preferences.AreQuietHoursEnabled
            ? $"{_preferences.QuietHoursStart:hh\\:mm}-{_preferences.QuietHoursEnd:hh\\:mm} on {_preferences.QuietHoursDays}"
            : "off";
        var snooze = _preferences.NotificationSnoozePreset == NotificationSnoozePreset.None
            ? "none"
            : $"{_preferences.NotificationSnoozePreset} until {Describe(WatchedPreferences.First(p => p.Name == nameof(IPreferencesService.NotificationSnoozeUntilUtcTicks)).Read(_preferences))}";

        _logger.Information("Sync coordinator ready: mode {Mode} (sync method {SyncMethod}, configured={Configured}), mail every {MailInterval} min, calendar every {CalendarInterval} min, relay availability {Availability}, socket connected={SocketConnected}, quiet hours {QuietHours}, snooze {Snooze}, snooze while presenting={SnoozeWhilePresenting}, new mail notifications={NewMailNotifications} ({Scope}, {Content}), calendar reminders={CalendarReminders}, task reminders={TaskReminders}.",
            snapshot.Mode, _preferences.SyncMethod, _preferences.IsSyncMethodConfigured, MailSyncInterval.TotalMinutes, CalendarSyncInterval.TotalMinutes,
            _relay?.Availability, snapshot.IsSocketConnected, quietHours, snooze, _preferences.SnoozeWhilePresenting,
            _preferences.AreNewMailNotificationsEnabled, _preferences.MailNotificationScope, _preferences.MailNotificationContent,
            _preferences.AreCalendarRemindersEnabled, _preferences.AreTaskRemindersEnabled);

        foreach (var account in accounts)
        {
            var coverage = snapshot.Coverage.TryGetValue(account.Id, out var c) ? c : null;
            var preferences = account.Preferences;
            _logger.Information("Account {AccountName} ({AccountId}, {Provider}): coverage {CoverageKind}/{CoverageGap}, attention {Attention}, notifications={Notifications}, custom={Custom}, new mail={NewMail}, scope {Scope}, quiet hours {QuietHoursStance}, calendar reminders={CalendarReminders}.",
                account.Name, account.Id, account.ProviderType, coverage?.Kind, coverage?.Gap, account.AttentionReason,
                preferences?.IsNotificationsEnabled, preferences?.HasCustomNotificationSettings, preferences?.IsNewMailNotificationEnabled,
                preferences?.NotificationScope, preferences?.QuietHoursStance, preferences?.AreCalendarRemindersEnabled);
        }
    }

    #endregion

    #region Helpers

    private IEnumerable<IWinoSynchronizerBase> EnumerateSynchronizers()
    {
        try { return _synchronizationManager.GetAllSynchronizers().ToList(); }
        catch (InvalidOperationException)
        {
            // Not initialized yet; IDLE statuses arrive through messages as the loops start.
            return [];
        }
    }

    private void RaiseChangedIfSocketStateDiffers()
    {
        var snapshot = Snapshot;
        bool differs;
        lock (_gate) differs = snapshot.IsSocketConnected != _rawSocketConnected;
        if (differs)
            Recompute("socket state");
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.Warning(ex, "A sync coordinator subscriber threw."); }
    }

    /// <summary>Called under <see cref="_gate"/>. Replaces any running timer in <paramref name="slot"/> with a one-shot timer.</summary>
    private ITimer StartTimer(ref ITimer? slot, TimeSpan dueTime, TimerCallback callback)
    {
        DisposeTimer(ref slot);
        return _timeProvider.CreateTimer(callback, null, dueTime, Timeout.InfiniteTimeSpan);
    }

    private static void DisposeTimer(ref ITimer? timer)
    {
        timer?.Dispose();
        timer = null;
    }

    private static string Describe(object? value)
        => value switch
        {
            null => "(none)",
            DateTimeOffset d => d.ToString("O"),
            TimeSpan t => t.ToString("c"),
            _ => value.ToString() ?? "(none)"
        };

    private sealed record AccountFacts(
        string Name,
        MailProviderType Provider,
        bool IsNetworkBlocked,
        bool IsMailAccessGranted,
        bool? NotificationsEnabled,
        bool? HasCustomNotificationSettings,
        bool? NewMailNotificationEnabled,
        MailNotificationScope? NotificationScope,
        MailNotificationContent? NotificationContent,
        AccountQuietHoursStance? QuietHoursStance,
        bool? CalendarRemindersEnabled)
    {
        public static AccountFacts From(MailAccount account)
        {
            var preferences = account.Preferences;
            return new AccountFacts(
                account.Name,
                account.ProviderType,
                account.IsNetworkAccessBlocked(),
                account.IsMailAccessGranted,
                preferences?.IsNotificationsEnabled,
                preferences?.HasCustomNotificationSettings,
                preferences?.IsNewMailNotificationEnabled,
                preferences?.NotificationScope,
                preferences?.NotificationContent,
                preferences?.QuietHoursStance,
                preferences?.AreCalendarRemindersEnabled);
        }

        public List<string> DescribeNotificationChanges(AccountFacts updated)
        {
            var changes = new List<string>();
            Add(changes, "notifications", NotificationsEnabled, updated.NotificationsEnabled);
            Add(changes, "custom settings", HasCustomNotificationSettings, updated.HasCustomNotificationSettings);
            Add(changes, "new mail", NewMailNotificationEnabled, updated.NewMailNotificationEnabled);
            Add(changes, "scope", NotificationScope, updated.NotificationScope);
            Add(changes, "content", NotificationContent, updated.NotificationContent);
            Add(changes, "quiet hours stance", QuietHoursStance, updated.QuietHoursStance);
            Add(changes, "calendar reminders", CalendarRemindersEnabled, updated.CalendarRemindersEnabled);
            return changes;
        }

        private static void Add<T>(List<string> changes, string label, T previous, T current)
        {
            if (!EqualityComparer<T>.Default.Equals(previous, current))
                changes.Add($"{label} {Describe(previous)} -> {Describe(current)}");
        }
    }

    #endregion
}
