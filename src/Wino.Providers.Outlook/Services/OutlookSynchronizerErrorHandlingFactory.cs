using Wino.Core.Domain.Interfaces;
using Wino.Core.Synchronizers.Errors;
using Wino.Core.Synchronizers.Errors.Outlook;

namespace Wino.Core.Services;

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
