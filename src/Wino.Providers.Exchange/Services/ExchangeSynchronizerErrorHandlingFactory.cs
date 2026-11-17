using Wino.Core.Domain.Interfaces;
using Wino.Core.Synchronizers.Errors;
using Wino.Core.Synchronizers.Errors.Exchange;

namespace Wino.Core.Services;

/// <summary>Routes Exchange synchronizer errors (EWS and MAPI over HTTP) to their handlers.</summary>
public class ExchangeSynchronizerErrorHandlingFactory : SynchronizerErrorHandlingFactory, IExchangeSynchronizerErrorHandlerFactory
{
    public ExchangeSynchronizerErrorHandlingFactory(
        ExchangeAuthenticationFailedHandler authenticationFailedHandler,
        ExchangeServerBusyHandler serverBusyHandler,
        ExchangeInvalidServerResponseHandler invalidServerResponseHandler,
        EntityNotFoundHandler entityNotFoundHandler)
    {
        // Order matters: the first handler that accepts an error owns it, so the most generic one goes last.
        RegisterHandler(authenticationFailedHandler);
        RegisterHandler(serverBusyHandler);
        RegisterHandler(invalidServerResponseHandler);
        RegisterHandler(entityNotFoundHandler);
    }
}
