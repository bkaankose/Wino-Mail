#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.PushRelay.Socket;

/// <summary>
/// The desktop app's socket to WinoRelay. One instance per app; <c>StartAsync</c> opens the socket for the signed-in
/// Wino Account and keeps it open (reconnecting as needed) until <c>StopAsync</c>.
/// </summary>
/// <remarks>
/// Every method runs on the thread pool and every event is raised on a thread-pool thread, never on the caller's
/// thread or a UI thread. Subscribers marshal to the UI themselves. Subscriber exceptions are logged and swallowed.
/// </remarks>
public interface IWinoRelaySocketClient : IAsyncDisposable
{
    /// <summary>True while the hub connection is open. Reads are lock-free.</summary>
    bool IsConnected { get; }

    RelaySocketConnectionState State { get; }

    /// <summary>The relay's greeting of the current connection, when it has arrived.</summary>
    RelaySocketHello? LastHello { get; }

    /// <summary>The socket is open and the relay sent <c>hello</c> (or the hello wait timed out: the argument is then null).</summary>
    event EventHandler<RelaySocketHello?>? Connected;

    /// <summary>One event, decrypted. Acknowledge it with <see cref="AckAsync"/> once it is processed.</summary>
    event EventHandler<RelaySocketEvent>? EventReceived;

    /// <summary>An event that could not be decrypted or parsed. It is left unacknowledged for the HTTP replay.</summary>
    event EventHandler<RelaySocketEventFailure>? EventFailed;

    /// <summary>The socket closed. <see cref="RelaySocketDisconnection.WillReconnect"/> says whether the client keeps trying.</summary>
    event EventHandler<RelaySocketDisconnection>? Disconnected;

    /// <summary>
    /// Opens the socket. Needs a registered relay device and a Wino Account that can get a relay token; the result
    /// says which precondition failed otherwise. Returns at once with <see cref="RelaySocketStartOutcome.AlreadyStarted"/>
    /// or <see cref="RelaySocketStartOutcome.AlreadyConnected"/> while a previous start is active.
    /// </summary>
    Task<RelaySocketStartResult> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the socket, stops reconnecting and disposes the connection. Safe to call when not started.</summary>
    Task StopAsync();

    /// <summary>Marks events as processed (same semantics as <c>POST /v1/devices/me/events/ack</c>). Returns how many were newly acknowledged.</summary>
    /// <exception cref="InvalidOperationException">The socket is not connected.</exception>
    /// <exception cref="ArgumentException">No ids, more than 500, or an id that is not a GUID.</exception>
    Task<int> AckAsync(IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken = default);
}
