#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;
using Wino.Core.Domain.Models.Settings;

namespace Wino.PushRelay.Socket;

/// <summary>
/// Desktop only. Keeps the relay socket open while the sync method is <see cref="SyncMethod.Relay"/> and the relay
/// device is registered, hands every event to <see cref="IPushRelayNotificationHandler"/>, acknowledges handled
/// events, and reports health for the shell indicator and the runtime's fallback loops.
/// </summary>
public sealed class RelaySocketCoordinator : IRelaySocketCoordinator
{
    private static readonly TimeSpan HandleBudget = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    private readonly IWinoRelaySocketClient _socket;
    private readonly IPushRelayRegistrationService _registration;
    private readonly IPushRelayNotificationHandler _handler;
    private readonly IPreferencesService _preferences;
    private readonly IPlatformCapabilities _capabilities;
    private readonly ILogger _logger = Log.ForContext<RelaySocketCoordinator>();
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _retry;
    private bool _started;
    private bool _socketConnected;
    private RelaySocketOutageKind _lastOutage;
    private bool _isReconnecting;
    private bool _reconciling;
    private bool _reconcileAgain;
    private bool? _wanted;
    private DateTimeOffset? _lastChange;
    private Guid? _revokedDeviceId;

    public RelaySocketCoordinator(IWinoRelaySocketClient socket, IPushRelayRegistrationService registration,
        IPushRelayNotificationHandler handler, IPreferencesService preferences, IPlatformCapabilities capabilities)
    {
        _socket = socket;
        _registration = registration;
        _handler = handler;
        _preferences = preferences;
        _capabilities = capabilities;
    }

    public bool IsSocketConnected { get { lock (_gate) return _socketConnected; } }

    public bool IsFallbackActive { get { lock (_gate) return _started && IsRelaySelected && !_socketConnected; } }

    public DateTimeOffset? LastChangeUtc { get { lock (_gate) return _lastChange; } }

    public RelaySocketOutageKind LastOutage { get { lock (_gate) return _lastOutage; } }

    public bool IsReconnecting { get { lock (_gate) return _isReconnecting; } }

    public event EventHandler? Changed;

    private bool IsRelaySelected
        => SettingAvailability.IsAvailable(PlatformSetting.SyncMethod, _capabilities) && _preferences.SyncMethod == SyncMethod.Relay;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_started) return Task.CompletedTask;
            _started = true;
            _lifetime = new CancellationTokenSource();
        }

        _preferences.PreferenceChanged += PreferenceChanged;
        _registration.SnapshotChanged += SnapshotChanged;
        _socket.Connected += SocketConnected;
        _socket.Disconnected += SocketDisconnected;
        _socket.EventReceived += SocketEventReceived;
        _socket.EventFailed += SocketEventFailed;

        RaiseChanged();
        return ReconcileAsync(cancellationToken);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? lifetime;
        lock (_gate)
        {
            if (!_started) return;
            _started = false;
            lifetime = _lifetime;
            _lifetime = null;
            _retry?.Cancel();
            _retry = null;
        }

        _preferences.PreferenceChanged -= PreferenceChanged;
        _registration.SnapshotChanged -= SnapshotChanged;
        _socket.Connected -= SocketConnected;
        _socket.Disconnected -= SocketDisconnected;
        _socket.EventReceived -= SocketEventReceived;
        _socket.EventFailed -= SocketEventFailed;

        lifetime?.Cancel();
        lifetime?.Dispose();
        await _socket.StopAsync().ConfigureAwait(false);
        lock (_gate) _wanted = null;
        SetState(false, RelaySocketOutageKind.None, false);
    }

    public async Task RequestReconnectAsync()
    {
        RelaySocketOutageKind outage;
        lock (_gate)
        {
            if (!_started) return;
            _retry?.Cancel();
            _retry = null;
            outage = _lastOutage;
        }

        var state = _socket.State;
        if (state == RelaySocketConnectionState.Connected) return;

        _logger.Information("Relay socket reconnect requested (state {State}, outage {Outage}).", state, outage);

        // The client is sleeping in its own backoff; stop it so the reconcile below starts it again right away.
        if (state == RelaySocketConnectionState.Reconnecting)
            await _socket.StopAsync().ConfigureAwait(false);

        await ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _reconcileLock.Dispose();
    }

    private void PreferenceChanged(object? sender, string propertyName)
    {
        if (propertyName != nameof(IPreferencesService.SyncMethod)) return;
        RaiseChanged();
        _ = ReconcileAsync(CancellationToken.None);
    }

    private void SnapshotChanged(object? sender, PushRelayRegistrationSnapshot snapshot) => _ = ReconcileAsync(CancellationToken.None);

    /// <summary>
    /// Opens or closes the socket so it matches the sync method and the registration state. A request that arrives
    /// while a pass is running is coalesced into one more pass after it, so no change is dropped.
    /// </summary>
    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_started || _lifetime is null) return;

            // Checked before cancelling _retry: a retry timer passes its own token, which the line below cancels.
            cancellationToken.ThrowIfCancellationRequested();
            _retry?.Cancel();
            _retry = null;
            if (_reconciling)
            {
                _reconcileAgain = true;
                return;
            }

            _reconciling = true;
            _reconcileAgain = false;
        }

        // _reconciling is cleared in the same lock that decides to stop, so a request that sets _reconcileAgain
        // is either seen by this loop or starts a loop of its own.
        try
        {
            while (true)
            {
                CancellationToken lifetime;
                lock (_gate)
                {
                    if (!_started || _lifetime is null)
                    {
                        _reconciling = false;
                        return;
                    }

                    lifetime = _lifetime.Token;
                }

                // Passes are bounded by the lifetime; _reconciling keeps this lock uncontended.
                await _reconcileLock.WaitAsync(lifetime).ConfigureAwait(false);
                try
                {
                    await ReconcilePassAsync(lifetime).ConfigureAwait(false);
                }
                finally
                {
                    _reconcileLock.Release();
                }

                lock (_gate)
                {
                    if (!_reconcileAgain)
                    {
                        _reconciling = false;
                        return;
                    }

                    _reconcileAgain = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
            lock (_gate) _reconciling = false;
        }
        catch
        {
            lock (_gate) _reconciling = false;
            throw;
        }
    }

    private async Task ReconcilePassAsync(CancellationToken lifetime)
    {
        try
        {
            var snapshot = _registration.Snapshot;
            string? blocker = !IsRelaySelected ? "sync method"
                : snapshot.Availability != PushRelayAvailability.Enabled ? "availability"
                : snapshot.DeviceId is null ? "no device"
                : null;
            var wanted = blocker is null;

            bool flipped;
            lock (_gate)
            {
                flipped = _wanted != wanted;
                _wanted = wanted;
            }

            if (!wanted)
            {
                if (flipped)
                    _logger.Information("Relay socket not wanted ({Blocker}); stopping it.", blocker);
                if (_socket.State != RelaySocketConnectionState.Stopped)
                    await _socket.StopAsync().ConfigureAwait(false);
                SetState(false, RelaySocketOutageKind.None, false);
                return;
            }

            if (flipped)
                _logger.Information("Relay socket wanted (sync method, availability and device ready); starting it.");

            if (_socket.State is RelaySocketConnectionState.Connected or RelaySocketConnectionState.Connecting or RelaySocketConnectionState.Reconnecting)
                return;

            var result = await _socket.StartAsync(lifetime).ConfigureAwait(false);
            if (result.IsConnected) return;

            var delay = result.RetryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : RetryDelay;
            if (result.Outcome == RelaySocketStartOutcome.DeviceNotRegistered)
            {
                _registration.RequestSync(PushRelaySyncReason.UserRequested);
            }
            else if (result.Problem?.Reason == PushRelayAuthReasons.DeviceRevoked)
            {
                // The relay no longer accepts this device: registration re-registers it and publishes the device, which
                // reconciles again. If that device is refused too, the next registration pass waits for a later attempt,
                // so the publish -> reconcile -> refusal cycle cannot spin.
                bool repeated;
                lock (_gate)
                {
                    repeated = _revokedDeviceId == snapshot.DeviceId;
                    _revokedDeviceId = repeated ? null : snapshot.DeviceId;
                }

                if (repeated)
                    delay = RetryDelay * 5;
                else
                    _registration.RequestSync(PushRelaySyncReason.DeviceRevoked);
            }

            _logger.Information("Relay socket did not open ({Outcome}/{Reason}); trying again in {Delay}.", result.Outcome, result.Problem?.Reason, delay);
            ScheduleRetry(delay, lifetime);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Relay socket reconcile failed; trying again in {Delay}.", RetryDelay);
            ScheduleRetry(RetryDelay, lifetime);
        }
    }

    private void ScheduleRetry(TimeSpan delay, CancellationToken lifetime)
    {
        CancellationTokenSource retry;
        lock (_gate)
        {
            if (!_started) return;
            _retry?.Cancel();
            retry = _retry = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, retry.Token).ConfigureAwait(false);
                await ReconcileAsync(retry.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        });
    }

    private void SocketConnected(object? sender, RelaySocketHello? hello)
    {
        _logger.Information("Relay socket connected.");
        lock (_gate) _revokedDeviceId = null;
        SetState(true, RelaySocketOutageKind.None, false);
    }

    private void SocketDisconnected(object? sender, RelaySocketDisconnection disconnection)
    {
        _logger.Information("Relay socket disconnected ({Reason}, retrying={Retrying}).", disconnection.Reason, disconnection.WillReconnect);
        switch (disconnection.Reason)
        {
            case RelaySocketDisconnectReason.ConnectionLost:
            case RelaySocketDisconnectReason.RelayRestarting:
                SetState(false, RelaySocketOutageKind.Transient, disconnection.WillReconnect);
                break;
            case RelaySocketDisconnectReason.Replaced when disconnection.WillReconnect:
                // A planned handover to the device's new owner, not an outage: it must not count as a flap.
                SetState(false, RelaySocketOutageKind.None, true);
                break;
            case RelaySocketDisconnectReason.Stopped:
                SetState(false, RelaySocketOutageKind.None, false);
                break;
            default:
                SetState(false, RelaySocketOutageKind.Terminal, false);
                break;
        }

        switch (disconnection.Reason)
        {
            case RelaySocketDisconnectReason.Revoked:
                _registration.RequestSync(PushRelaySyncReason.DeviceRevoked);
                break;
            case RelaySocketDisconnectReason.SignInRequired:
            case RelaySocketDisconnectReason.Refused:
            case RelaySocketDisconnectReason.Replaced when !disconnection.WillReconnect:
            case RelaySocketDisconnectReason.Abuse:
                // The client stays down on these; look again later in case the state changed.
                lock (_gate) { if (_lifetime is { } lifetime) ScheduleRetry(RetryDelay * 5, lifetime.Token); }
                break;
        }
    }

    private void SocketEventReceived(object? sender, RelaySocketEvent relayEvent) => _ = HandleEventAsync(relayEvent);

    private async Task HandleEventAsync(RelaySocketEvent relayEvent)
    {
        var outcome = PushRelayNotificationOutcome.Failed;
        try
        {
            // The sync the event starts is never cancelled by the budget; the ack only stops waiting for it.
            outcome = await _handler.HandleAsync(relayEvent.Payload, CancellationToken.None).WaitAsync(HandleBudget).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.Debug("Relay socket event {EventId} ({Kind}) is still being handled; acknowledging it.", relayEvent.EventId, relayEvent.Kind);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Relay socket event {EventId} ({Kind}) could not be handled.", relayEvent.EventId, relayEvent.Kind);
        }

        // Ignored and NoData events are done; a failed sync is acknowledged too, because the periodic
        // fallback and the next push cover it and an unacked event would only replay the same work.
        if (string.IsNullOrEmpty(relayEvent.EventId) || !_socket.IsConnected) return;
        try
        {
            await _socket.AckAsync(new[] { relayEvent.EventId }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Relay event {EventId} was handled ({Outcome}) but not acknowledged.", relayEvent.EventId, outcome);
        }
    }

    private void SocketEventFailed(object? sender, RelaySocketEventFailure failure)
        => _logger.Warning(failure.Error, "Relay socket event could not be read: {Reason}.", failure.Reason);

    private void SetState(bool connected, RelaySocketOutageKind outage, bool reconnecting)
    {
        lock (_gate)
        {
            if (_socketConnected == connected && _lastOutage == outage && _isReconnecting == reconnecting) return;
            if (_socketConnected != connected)
            {
                _socketConnected = connected;
                _lastChange = DateTimeOffset.UtcNow;
            }

            _lastOutage = outage;
            _isReconnecting = reconnecting;
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.Warning(ex, "A relay health subscriber threw."); }
    }
}
