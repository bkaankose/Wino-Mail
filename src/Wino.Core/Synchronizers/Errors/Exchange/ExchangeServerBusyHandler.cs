using System;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task.
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Synchronizers.Errors.Exchange;

/// <summary>Handles EWS throttling and server-provided backoff.</summary>
public class ExchangeServerBusyHandler : ISynchronizerErrorHandler
{
    private readonly ILogger _logger = Log.ForContext<ExchangeServerBusyHandler>();

    public bool CanHandle(SynchronizerErrorContext error)
        => error.ErrorCode == 429 || error.Exception is ServerBusyException;

    public Task<bool> HandleAsync(SynchronizerErrorContext error)
    {
        var backoff = error.Exception is ServerBusyException busy && busy.BackOffMilliseconds > 0
            ? TimeSpan.FromMilliseconds(busy.BackOffMilliseconds)
            : TimeSpan.FromSeconds(10);

        _logger.Warning(error.Exception,
            "Exchange throttled account {AccountName} ({AccountId}); backing off {Backoff}.",
            error.Account?.Name, error.Account?.Id, backoff);

        error.Severity = SynchronizerErrorSeverity.Transient;
        error.Category = SynchronizerErrorCategory.RateLimit;
        error.RetryDelay = backoff;

        return Task.FromResult(true);
    }
}
