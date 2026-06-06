using Wino.Core.Domain.Interfaces;
using Wino.Core.Synchronizers.Errors;

namespace Wino.Core.Services;

/// <summary>
/// Factory for handling on-premises Exchange (EWS) synchronizer errors.
/// No handlers are registered yet; Phase 1 adds auth-failed, throttling (429 /
/// ServerBusyException), and not-found handlers following the IMAP/Outlook pattern.
/// </summary>
public class ExchangeSynchronizerErrorHandlingFactory : SynchronizerErrorHandlingFactory, IExchangeSynchronizerErrorHandlerFactory
{
}
