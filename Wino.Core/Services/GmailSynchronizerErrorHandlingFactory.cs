using Wino.Core.Domain.Interfaces;
using Wino.Core.Synchronizers.Errors;
using Wino.Core.Synchronizers.Errors.Gmail;

namespace Wino.Core.Services;

/// <summary>
/// Factory for handling Gmail synchronizer errors.
/// Registers and routes errors to appropriate handlers.
/// </summary>
public class GmailSynchronizerErrorHandlingFactory : SynchronizerErrorHandlingFactory, IGmailSynchronizerErrorHandlerFactory
{
    public GmailSynchronizerErrorHandlingFactory(
        GmailQuotaExceededHandler quotaExceededHandler,
        GmailRateLimitHandler rateLimitHandler,
        GmailHistoryExpiredHandler historyExpiredHandler,
        EntityNotFoundHandler entityNotFoundHandler)
    {
        // Order matters - more specific handlers should be registered first
        RegisterHandler(quotaExceededHandler);
        RegisterHandler(historyExpiredHandler);
        RegisterHandler(entityNotFoundHandler);
        RegisterHandler(rateLimitHandler); // Most generic rate limit handler last
    }
}
