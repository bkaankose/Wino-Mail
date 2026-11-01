using System;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration.Processors;
using Wino.Core.Synchronizers.Mapi;

namespace Wino.Core.Synchronizers.Exchange;

/// <summary>
/// Picks the synchronizer for an on-premises Exchange account. The account's transport choice wins; under Automatic,
/// MAPI over HTTP is used unless Autodiscover was found not to advertise it. The MAPI synchronizer records that
/// finding on the account, so the next synchronizer built for it is the EWS one.
/// </summary>
public static class ExchangeSynchronizerSelector
{
    public static ExchangeSynchronizer Create(MailAccount account,
                                              IExchangeAuthenticator exchangeAuthenticator,
                                              IExchangeChangeProcessor exchangeChangeProcessor,
                                              IExchangeSynchronizerErrorHandlerFactory errorHandlerFactory,
                                              IContactService? contactService = null,
                                              IPictureStorageService? contactPictureService = null,
                                              ITaskService? taskService = null)
    {
        ArgumentNullException.ThrowIfNull(account);

        var serverInformation = account.ServerInformation
            ?? throw new InvalidOperationException("The Exchange account has no server information.");

        return serverInformation.EffectiveExchangeTransport == ExchangeTransport.Ews
            ? new ExchangeSynchronizer(account, exchangeAuthenticator, exchangeChangeProcessor, errorHandlerFactory, contactService, contactPictureService, taskService)
            : new MapiExchangeSynchronizer(account, exchangeAuthenticator, exchangeChangeProcessor, errorHandlerFactory, contactService, contactPictureService, taskService);
    }
}
