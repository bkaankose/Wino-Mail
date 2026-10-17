#nullable enable
using System;

namespace Wino.PushRelay.Socket;

/// <summary>The wire names of WinoRelay's desktop hub (<c>WinoRelay.Sockets.DeviceHub</c>).</summary>
public static class RelaySocketProtocol
{
    /// <summary>Hub path used when the well-known document names none.</summary>
    public const string DefaultHubPath = "/v1/socket";

    /// <summary>Header carrying the relay device id, as on the REST API.</summary>
    public const string DeviceIdHeaderName = "X-Device-Id";

    /// <summary>Server to client, once after connect.</summary>
    public const string HelloMethod = "hello";

    /// <summary>Server to client: the push payload dictionary or its encrypted envelope.</summary>
    public const string EventMethod = "event";

    /// <summary>Server to client, right before the relay closes the socket.</summary>
    public const string ByeMethod = "bye";

    /// <summary>Client to server: marks events as processed; returns how many were newly acknowledged.</summary>
    public const string AckMethod = "Ack";

    /// <summary>Most event ids one <see cref="AckMethod"/> call may carry.</summary>
    public const int MaxAckIds = 500;
}

/// <summary>Reasons the relay sends with <see cref="RelaySocketProtocol.ByeMethod"/>.</summary>
public static class RelaySocketByeReasons
{
    /// <summary>The device was revoked or deleted; register again before connecting.</summary>
    public const string Revoked = "revoked";

    /// <summary>A newer socket of the same device replaced this one.</summary>
    public const string Replaced = "replaced";

    /// <summary>The relay instance is restarting; reconnect to a live replica.</summary>
    public const string Restart = "restart";

    /// <summary>The socket misbehaved and the device is banned for a while.</summary>
    public const string Abuse = "abuse";
}

/// <summary>Values of the <c>reason</c> extension on socket connect refusals.</summary>
public static class RelaySocketRefusalReasons
{
    public const string PlatformNotAllowed = "socket_platform";
    public const string KeyRequired = "socket_key_required";
    public const string Banned = "socket_banned";
    public const string Capacity = "socket_capacity";
    public const string LimitPerIp = "socket_limit:per_ip";
    public const string LimitPerAccount = "socket_limit:per_account";
}

/// <summary>Keys of the encrypted event envelope.</summary>
public static class RelaySocketEnvelopeKeys
{
    /// <summary>Envelope version; present only on encrypted events.</summary>
    public const string Version = "enc";
    public const string KeyId = "kid";
    public const string Nonce = "n";
    public const string Ciphertext = "c";

    /// <summary>The event id, which is also the AES-GCM associated data.</summary>
    public const string EventId = "eid";

    /// <summary>The only envelope version this client decrypts.</summary>
    public const string SupportedVersion = "1";
}
