using Wino.Core.Domain.Interfaces;
using Wino.Core.Synchronizers.Errors;
using Wino.Core.Services;
using Wino.Providers.Outlook.Interfaces;
using Wino.Providers.Outlook.Synchronizers.Errors.Outlook;

namespace Wino.Providers.Outlook.Services;

public class OutlookSynchronizerErrorHandlingFactory : SynchronizerErrorHandlingFactory, IOutlookSynchronizerErrorHandlerFactory
{
    public OutlookSynchronizerErrorHandlingFactory(OutlookAuthenticationFailedHandler authenticationFailedHandler,
                                                 ObjectCannotBeDeletedHandler objectCannotBeDeleted,
                                                 EntityNotFoundHandler entityNotFoundHandler,
                                                 DeltaTokenExpiredHandler deltaTokenExpiredHandler,
                                                 OutlookRateLimitHandler outlookRateLimitHandler)
    {
        RegisterHandler(authenticationFailedHandler);
        RegisterHandler(outlookRateLimitHandler);
        RegisterHandler(objectCannotBeDeleted);
        RegisterHandler(entityNotFoundHandler);
        RegisterHandler(deltaTokenExpiredHandler);
    }
}
