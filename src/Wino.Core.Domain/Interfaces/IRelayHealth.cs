#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Whether live updates are flowing right now. The shell shows a red dot only while <see cref="IsFallbackActive"/>.
/// The registered implementation is the <see cref="ISyncCoordinator"/>, which applies hysteresis to the raw socket
/// state; the socket coordinator implements it too, as the raw input.
/// </summary>
public interface IRelayHealth
{
    /// <summary>The relay socket is open and events can arrive.</summary>
    bool IsSocketConnected { get; }

    /// <summary>Relay-based sync is selected but live coverage is not trustworthy, so every account syncs on the user's intervals.</summary>
    bool IsFallbackActive { get; }

    /// <summary>When the socket connected or dropped last, when known.</summary>
    DateTimeOffset? LastChangeUtc { get; }

    /// <summary>Raised on a background thread whenever <see cref="IsSocketConnected"/> or <see cref="IsFallbackActive"/> changes.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Owns the desktop relay socket: opens it while the sync method is <see cref="Enums.SyncMethod.Relay"/> and the relay
/// device is registered, routes its events to <see cref="IPushRelayNotificationHandler"/>, and closes it otherwise.
/// Mobile heads register no coordinator.
/// </summary>
public interface IRelaySocketCoordinator : IRelayHealth, IAsyncDisposable
{
    /// <summary>Subscribes to the sync method and the relay registration state, then connects when both allow it.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();

    /// <summary>What the most recent disconnect means; <see cref="Enums.RelaySocketOutageKind.None"/> while connected.</summary>
    Enums.RelaySocketOutageKind LastOutage { get; }

    /// <summary>The socket client dropped and is retrying on its own schedule.</summary>
    bool IsReconnecting { get; }

    /// <summary>
    /// Connects again now when the socket is wanted but not connected, bypassing the client's own reconnect delay.
    /// Used after the network comes back. A no-op while connected.
    /// </summary>
    Task RequestReconnectAsync();
}
