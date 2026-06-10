using System;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Ipc.Protocol;

namespace Wino.Ipc.Contracts;

/// <summary>
/// Maps the domain exceptions the UI explicitly catches across the process boundary.
/// Server side: exception → error envelope with a well-known key and serialized state.
/// Client side: error envelope → rehydrated domain exception.
/// Anything unmapped becomes a generic <see cref="WinoRpcRemoteException"/> on the client.
/// </summary>
public static class WinoRpcDomainExceptions
{
    private const string InteractiveAuthRequiredKey = "InteractiveAuthRequired";
    private const string UnavailableSpecialFolderKey = "UnavailableSpecialFolder";
    private const string AccountSetupCanceledKey = "AccountSetupCanceled";
    private const string InvalidMoveTargetKey = "InvalidMoveTarget";



    /// <summary>
    /// Server-side mapping used by the RPC connection's exception mapper.
    /// </summary>
    public static RpcErrorEnvelope ToErrorEnvelope(Exception exception)
    {
        switch (exception)
        {
            case OperationCanceledException:
                return new RpcErrorEnvelope(RpcErrorTypes.Canceled, exception.Message);

            case InteractiveAuthRequiredException interactiveAuth:
                return new RpcErrorEnvelope(RpcErrorTypes.Exception,
                                            interactiveAuth.Message,
                                            interactiveAuth.GetType().FullName,
                                            interactiveAuth.StackTrace,
                                            InteractiveAuthRequiredKey,
                                            WinoIpcJson.SerializeToString(new InteractiveAuthRequiredState(interactiveAuth.AccountId, interactiveAuth.Message)));

            case AuthenticationAttentionException authenticationAttention:
                // The account entity does not need to cross; the UI re-resolves it by id.
                return new RpcErrorEnvelope(RpcErrorTypes.Exception,
                                            authenticationAttention.Message,
                                            authenticationAttention.GetType().FullName,
                                            authenticationAttention.StackTrace,
                                            InteractiveAuthRequiredKey,
                                            WinoIpcJson.SerializeToString(new InteractiveAuthRequiredState(authenticationAttention.Account?.Id ?? Guid.Empty, authenticationAttention.Message)));

            case UnavailableSpecialFolderException unavailableSpecialFolder:
                return new RpcErrorEnvelope(RpcErrorTypes.Exception,
                                            unavailableSpecialFolder.Message,
                                            unavailableSpecialFolder.GetType().FullName,
                                            unavailableSpecialFolder.StackTrace,
                                            UnavailableSpecialFolderKey,
                                            WinoIpcJson.SerializeToString(new UnavailableSpecialFolderState(unavailableSpecialFolder.SpecialFolderType, unavailableSpecialFolder.AccountId)));

            case AccountSetupCanceledException:
                return new RpcErrorEnvelope(RpcErrorTypes.Exception,
                                            exception.Message,
                                            exception.GetType().FullName,
                                            exception.StackTrace,
                                            AccountSetupCanceledKey);

            case InvalidMoveTargetException invalidMoveTarget:
                return new RpcErrorEnvelope(RpcErrorTypes.Exception,
                                            invalidMoveTarget.Message,
                                            invalidMoveTarget.GetType().FullName,
                                            invalidMoveTarget.StackTrace,
                                            InvalidMoveTargetKey,
                                            WinoIpcJson.SerializeToString(new InvalidMoveTargetState(invalidMoveTarget.Reason)));

            default:
                return new RpcErrorEnvelope(RpcErrorTypes.Exception,
                                            exception.Message,
                                            exception.GetType().FullName,
                                            exception.StackTrace);
        }
    }

    /// <summary>
    /// Client-side mapping passed to <see cref="RpcClient"/>. Returns null for unmapped errors.
    /// </summary>
    public static Exception? ToException(RpcErrorEnvelope error)
    {
        switch (error.DomainExceptionKey)
        {
            case InteractiveAuthRequiredKey:
            {
                var state = DeserializeState<InteractiveAuthRequiredState>(error);
                return new InteractiveAuthRequiredException(state?.AccountId ?? Guid.Empty, state?.Message ?? error.Message);
            }

            case UnavailableSpecialFolderKey:
            {
                var state = DeserializeState<UnavailableSpecialFolderState>(error);
                return new UnavailableSpecialFolderException(state?.SpecialFolderType ?? SpecialFolderType.Other, state?.AccountId ?? Guid.Empty);
            }

            case AccountSetupCanceledKey:
                return new AccountSetupCanceledException();

            case InvalidMoveTargetKey:
            {
                var state = DeserializeState<InvalidMoveTargetState>(error);
                return new InvalidMoveTargetException(state?.Reason ?? default);
            }

            default:
                return null;
        }
    }

    private static TState? DeserializeState<TState>(RpcErrorEnvelope error) where TState : class
        => error.DomainExceptionPayloadJson == null
            ? null
            : WinoIpcJson.DeserializeFromString<TState>(error.DomainExceptionPayloadJson);
}
