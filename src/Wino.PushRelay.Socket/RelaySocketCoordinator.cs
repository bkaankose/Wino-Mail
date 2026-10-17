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
    private DateTimeOffset? _lastChange;

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
        SetConnected(false);
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

    /// <summary>Opens or closes the socket so it matches the sync method and the registration state.</summary>
    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        CancellationToken lifetime;
        lock (_gate)
        {
            if (!_started || _lifetime is null) return;
            lifetime = _lifetime.Token;
            _retry?.Cancel();
            _retry = null;
        }

        if (!await _reconcileLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            var snapshot = _registration.Snapshot;
            var wanted = IsRelaySelected && snapshot.Availability == PushRelayAvailability.Enabled && snapshot.DeviceId is not null;

            if (!wanted)
            {
                if (_socket.State != RelaySocketConnectionState.Stopped)
                    await _socket.StopAsync().ConfigureAwait(false);
                SetConnected(false);
                return;
            }

            if (_socket.State is RelaySocketConnectionState.Connected or RelaySocketConnectionState.Connecting or RelaySocketConnectionState.Reconnecting)
                return;

            var result = await _socket.StartAsync(lifetime).ConfigureAwait(false);
            if (result.IsConnected) return;

            _logger.Information("Relay socket did not open ({Outcome}); trying again in {Delay}.", result.Outcome, RetryDelay);
            if (result.Outcome == RelaySocketStartOutcome.DeviceNotRegistered)
                _registration.RequestSync(PushRelaySyncReason.UserRequested);
            ScheduleRetry(result.RetryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : RetryDelay, lifetime);
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
        finally
        {
            _reconcileLock.Release();
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

    private void SocketConnected(object? sender, RelaySocketHello? hello) => SetConnected(true);

    private void SocketDisconnected(object? sender, RelaySocketDisconnection disconnection)
    {
        SetConnected(false);
        switch (disconnection.Reason)
        {
            case RelaySocketDisconnectReason.Revoked:
                _registration.RequestSync(PushRelaySyncReason.DeviceRevoked);
                break;
            case RelaySocketDisconnectReason.SignInRequired:
            case RelaySocketDisconnectReason.Refused:
            case RelaySocketDisconnectReason.Replaced:
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
            using var budget = new CancellationTokenSource(HandleBudget);
            outcome = await _handler.HandleAsync(relayEvent.Payload, budget.Token).ConfigureAwait(false);
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

    private void SetConnected(bool connected)
    {
        lock (_gate)
        {
            if (_socketConnected == connected) return;
            _socketConnected = connected;
            _lastChange = DateTimeOffset.UtcNow;
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.Warning(ex, "A relay health subscriber threw."); }
    }
}
