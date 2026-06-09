using Wino.Core.Domain.Interfaces;
using Wino.Core.Synchronizers.Errors;
using Wino.Core.Synchronizers.Errors.Exchange;

namespace Wino.Core.Services;

/// <summary>
/// Factory for handling on-premises Exchange (EWS) synchronizer errors.
/// More specific handlers are registered first.
/// </summary>
public class ExchangeSynchronizerErrorHandlingFactory : SynchronizerErrorHandlingFactory, IExchangeSynchronizerErrorHandlerFactory
{
    public ExchangeSynchronizerErrorHandlingFactory(
        ExchangeAuthenticationFailedHandler authenticationFailedHandler,
        ExchangeServerBusyHandler serverBusyHandler,
        ExchangeInvalidServerResponseHandler invalidServerResponseHandler,
        EntityNotFoundHandler entityNotFoundHandler)
    {
        RegisterHandler(authenticationFailedHandler);
        RegisterHandler(serverBusyHandler);
        RegisterHandler(invalidServerResponseHandler);
        RegisterHandler(entityNotFoundHandler); // most generic, registered last
    }
}
