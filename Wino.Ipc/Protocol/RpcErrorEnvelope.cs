namespace Wino.Ipc.Protocol;

public static class RpcErrorTypes
{
    /// <summary>The remote call was canceled (either via a Cancel frame or server-side cancellation).</summary>
    public const string Canceled = "canceled";

    /// <summary>The remote call threw an exception.</summary>
    public const string Exception = "exception";

    /// <summary>The request envelope could not be parsed or the method is unknown.</summary>
    public const string Protocol = "protocol";
}

/// <summary>
/// Payload of a <see cref="FrameType.ResponseError"/> frame.
/// <see cref="DomainExceptionKey"/> carries an optional well-known key for domain exceptions
/// the UI explicitly catches (e.g. interactive auth required); the contracts layer maps it
/// back to the original exception type on the client.
/// </summary>
public sealed record RpcErrorEnvelope(
    string ErrorType,
    string? Message,
    string? RemoteExceptionType = null,
    string? RemoteStackTrace = null,
    string? DomainExceptionKey = null,
    string? DomainExceptionPayloadJson = null);
