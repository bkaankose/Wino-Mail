#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Whether live updates are flowing right now. Desktop heads back it with the relay socket; the shell shows a red dot
/// only while <see cref="IsFallbackActive"/>, and <c>ApplicationRuntime</c> runs its 5 minute fallback loops then.
/// </summary>
public interface IRelayHealth
{
    /// <summary>The relay socket is open and events can arrive.</summary>
    bool IsSocketConnected { get; }

    /// <summary>Relay-based sync is selected but the socket is down, so the fixed timer covers mail and calendar.</summary>
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
}
