using Wino.AppServices.Contracts;

namespace Wino.Mail.Uwp.Services;

public sealed class WinoRemoteException : Exception
{
    public WinoRemoteException(RpcResponse response)
        : base(response.ErrorMessage ?? $"Companion RPC failed with {response.ErrorCode}.")
    {
        ErrorCode = response.ErrorCode;
        RequiredUserAction = response.RequiredUserAction;
    }

    public RpcErrorCode ErrorCode { get; }
    public RequiredUserAction RequiredUserAction { get; }
}
