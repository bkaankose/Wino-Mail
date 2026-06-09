namespace Wino.Ipc.Protocol;

/// <summary>
/// First frame payload a client sends after connecting.
/// </summary>
public sealed record HandshakeRequest(int ProtocolVersion, string AppVersion, string ClientName);

/// <summary>
/// Server response to <see cref="HandshakeRequest"/>. When <see cref="Accepted"/> is false the
/// client is expected to ask the (older) server to terminate, relaunch it and reconnect.
/// </summary>
public sealed record HandshakeResponse(int ProtocolVersion, string AppVersion, bool Accepted, string? Message = null);
