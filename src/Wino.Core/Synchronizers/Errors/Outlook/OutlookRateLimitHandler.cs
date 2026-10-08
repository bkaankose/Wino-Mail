using System;
using System.Threading.Tasks;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Synchronizers.Errors.Outlook;

/// <summary>
/// Handles Microsoft Graph throttling responses for Outlook synchronization.
/// </summary>
public class OutlookRateLimitHandler : ISynchronizerErrorHandler
{
    private readonly ILogger _logger = Log.ForContext<OutlookRateLimitHandler>();

    public bool CanHandle(SynchronizerErrorContext error)
    {
        return error.ErrorCode == 429 ||
               (error.Exception is ODataError oDataError && oDataError.ResponseStatusCode == 429) ||
               (error.Exception is ApiException apiException && apiException.ResponseStatusCode == 429);
    }

    public Task<bool> HandleAsync(SynchronizerErrorContext error)
    {
        _logger.Warning(error.Exception,
            "Microsoft Graph rate limit hit for account {AccountName} ({AccountId}). Operation: {Operation}.",
            error.Account?.Name, error.Account?.Id, error.OperationType ?? "N/A");

        error.Severity = SynchronizerErrorSeverity.Transient;
        error.Category = SynchronizerErrorCategory.RateLimit;
        error.RetryDelay = TimeSpan.FromSeconds(10);

        return Task.FromResult(true);
    }
}
