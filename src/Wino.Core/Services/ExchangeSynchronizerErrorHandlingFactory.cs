using Wino.Core.Domain.Interfaces;
using Wino.Core.Synchronizers.Errors;
using Wino.Core.Synchronizers.Errors.Exchange;

namespace Wino.Core.Services;

/// <summary>
/// Factory for handling Exchange synchronizer errors (EWS and MAPI/HTTP).
/// Registers and routes errors to appropriate handlers.
/// </summary>
public class ExchangeSynchronizerErrorHandlingFactory : SynchronizerErrorHandlingFactory, IExchangeSynchronizerErrorHandlerFactory
{
    public ExchangeSynchronizerErrorHandlingFactory(
        ExchangeAuthenticationFailedHandler authenticationFailedHandler,
        ExchangeServerBusyHandler serverBusyHandler,
        ExchangeInvalidServerResponseHandler invalidServerResponseHandler,
        EntityNotFoundHandler entityNotFoundHandler)
    {
        // Order matters - more specific handlers should be registered first
        RegisterHandler(authenticationFailedHandler);
        RegisterHandler(serverBusyHandler);
        RegisterHandler(invalidServerResponseHandler);
        RegisterHandler(entityNotFoundHandler); // most generic, registered last
    }
}
