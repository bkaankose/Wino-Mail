using System;
using Wino.Ipc.Protocol;

namespace Wino.Ipc;

/// <summary>
/// Thrown on the client when a remote call fails with an exception that has no
/// dedicated domain mapping.
/// </summary>
public class WinoRpcRemoteException : Exception
{
    public WinoRpcRemoteException(RpcErrorEnvelope error)
        : base(error.Message ?? "Remote call failed.")
    {
        Error = error;
    }

    public RpcErrorEnvelope Error { get; }

    public string? RemoteExceptionType => Error.RemoteExceptionType;

    public string? RemoteStackTrace => Error.RemoteStackTrace;
}

/// <summary>
/// Thrown when the connection drops while a call is in flight. The connection layer
/// treats this as retriable for idempotent calls.
/// </summary>
public class WinoRpcConnectionLostException : Exception
{
    public WinoRpcConnectionLostException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
