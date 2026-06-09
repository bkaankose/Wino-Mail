namespace Wino.Ipc;

/// <summary>
/// Wire-level frame discriminator. Stored as a single byte on the wire.
/// </summary>
public enum FrameType : byte
{
    /// <summary>Client → server. First frame on a connection; payload is <c>HandshakeRequest</c>.</summary>
    Handshake = 1,

    /// <summary>Server → client. Response to <see cref="Handshake"/>; payload is <c>HandshakeResponse</c>.</summary>
    HandshakeAck = 2,

    /// <summary>Client → server. RPC call; payload is a request envelope.</summary>
    Request = 3,

    /// <summary>Server → client. Successful RPC result; payload is the raw response record JSON.</summary>
    ResponseSuccess = 4,

    /// <summary>Server → client. Failed RPC; payload is <c>RpcErrorEnvelope</c>.</summary>
    ResponseError = 5,

    /// <summary>Client → server. Requests cancellation of the in-flight call with the same correlation id. No payload.</summary>
    Cancel = 6,

    /// <summary>Server → client. Forwarded UI message; payload is an event envelope.</summary>
    Event = 7,

    /// <summary>Server → client. Partial chunk of an oversized successful response payload.</summary>
    StreamChunk = 8,

    /// <summary>Server → client. Terminates a chunked response; payload is the final chunk (possibly empty).</summary>
    StreamEnd = 9,

    /// <summary>Health probe. No payload.</summary>
    Ping = 10,

    /// <summary>Health probe response. No payload.</summary>
    Pong = 11,
}
