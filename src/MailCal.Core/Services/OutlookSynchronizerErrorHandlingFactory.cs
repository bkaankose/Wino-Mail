using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Synchronizers.Errors;
using MailCal.Core.Synchronizers.Errors.Outlook;

namespace MailCal.Core.Services;

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
