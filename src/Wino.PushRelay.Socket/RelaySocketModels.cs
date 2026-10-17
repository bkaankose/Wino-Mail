#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.PushRelay.Socket;

/// <summary>What the relay sends right after a socket connects.</summary>
public sealed record RelaySocketHello(
    [property: JsonPropertyName("serverTime")] DateTimeOffset ServerTime,
    [property: JsonPropertyName("unackedEvents")] int UnackedEvents,
    [property: JsonPropertyName("retentionHours")] int RetentionHours);

/// <summary>
/// One event delivered over the socket, or replayed from the device event log right after connecting: exactly the
/// dictionary a push would carry, decrypted when the relay encrypted it. <see cref="EventId"/> is the value to acknowledge.
/// <see cref="IsReplayed"/> marks events fetched over HTTP (<c>GET /v1/devices/me/events?unacked=true</c>) because they
/// were sent while no socket was open; <see cref="ReceivedAtUtc"/> is then the relay's send time.
/// </summary>
public sealed record RelaySocketEvent(
    string? EventId,
    string? Kind,
    IReadOnlyDictionary<string, string> Payload,
    bool WasEncrypted,
    DateTimeOffset ReceivedAtUtc,
    bool IsReplayed = false)
{
    public string? ClientAccountId => Payload.TryGetValue(PushRelayPayloadKeys.ClientAccountId, out var value) ? value : null;
}

/// <summary>An event that arrived but could not be turned into a <see cref="RelaySocketEvent"/>; it is not acknowledged.</summary>
public sealed record RelaySocketEventFailure(string Reason, Exception? Error, IReadOnlyDictionary<string, string> Message);

public enum RelaySocketConnectionState
{
    Stopped,
    Connecting,
    Connected,

    /// <summary>The socket dropped and the client is waiting to connect again.</summary>
    Reconnecting
}

public enum RelaySocketStartOutcome
{
    Connected,

    /// <summary>A previous <c>StartAsync</c> is still connected.</summary>
    AlreadyConnected,

    /// <summary>A previous <c>StartAsync</c> is connecting or reconnecting.</summary>
    AlreadyStarted,

    /// <summary>No Wino Account session, or the Wino API refused to issue a relay token.</summary>
    SignInRequired,

    /// <summary>This install has no relay device id yet (the registration service has not registered one).</summary>
    DeviceNotRegistered,

    /// <summary>The relay encrypts socket payloads but this head has no socket key store or key.</summary>
    SocketKeyMissing,

    /// <summary>The relay's well-known document reports no socket hub.</summary>
    SocketUnavailable,

    /// <summary>The relay refused the connection (403 or 429); <see cref="RelaySocketStartResult.Problem"/> says why.</summary>
    Refused,

    /// <summary>The relay or the Wino API could not be reached, or answered with a server error.</summary>
    Unavailable
}

/// <summary>The outcome of <c>StartAsync</c>. <see cref="Hello"/> is set when the relay's greeting arrived in time.</summary>
public sealed record RelaySocketStartResult(
    RelaySocketStartOutcome Outcome,
    PushRelayProblem? Problem = null,
    RelaySocketHello? Hello = null,
    Exception? Error = null)
{
    public bool IsConnected => Outcome is RelaySocketStartOutcome.Connected or RelaySocketStartOutcome.AlreadyConnected;

    /// <summary>Seconds the relay asked the client to wait before trying again, when it said so.</summary>
    public int? RetryAfterSeconds => Problem?.RetryAfterSeconds;
}

public enum RelaySocketDisconnectReason
{
    /// <summary><c>StopAsync</c> or disposal.</summary>
    Stopped,

    /// <summary>The transport dropped without a <c>bye</c>; the client reconnects.</summary>
    ConnectionLost,

    /// <summary><c>bye revoked</c>: the device must register again.</summary>
    Revoked,

    /// <summary><c>bye replaced</c>: a newer socket of this device took over, so this client stays down.</summary>
    Replaced,

    /// <summary><c>bye restart</c>: the relay instance is restarting; the client reconnects.</summary>
    RelayRestarting,

    /// <summary><c>bye abuse</c>: banned for a while; the client stays down.</summary>
    Abuse,

    /// <summary>The relay token could not be renewed because the Wino Account session is gone.</summary>
    SignInRequired,

    /// <summary>A reconnect attempt was refused for good (platform, key, capacity after retries).</summary>
    Refused
}

public sealed record RelaySocketDisconnection(
    RelaySocketDisconnectReason Reason,
    bool WillReconnect,
    string? ByeReason = null,
    Exception? Error = null,
    PushRelayProblem? Problem = null);
