using System;
using System.Threading.Tasks;
using Google;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Synchronizers.Errors.Gmail;

/// <summary>
/// Handles Gmail API rate limiting errors (HTTP 429 Too Many Requests).
/// Marks the error as transient with appropriate backoff delay.
/// </summary>
public class GmailRateLimitHandler : ISynchronizerErrorHandler
{
    private readonly ILogger _logger = Log.ForContext<GmailRateLimitHandler>();

    public bool CanHandle(SynchronizerErrorContext error)
    {
        if (error.ErrorCode == 429)
            return true;

        if (error.Exception is GoogleApiException googleEx)
        {
            return googleEx.HttpStatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                   (googleEx.Error?.Code == 429);
        }

        return false;
    }

    public Task<bool> HandleAsync(SynchronizerErrorContext error)
    {
        _logger.Warning(error.Exception,
            "Gmail API rate limit hit for account {AccountName} ({AccountId}). Operation: {Operation}. Will retry with backoff.",
            error.Account?.Name, error.Account?.Id, error.OperationType ?? "N/A");

        error.Severity = SynchronizerErrorSeverity.Transient;
        error.Category = SynchronizerErrorCategory.RateLimit;

        // Gmail rate limits are usually per-user, suggest a longer delay
        error.RetryDelay = TimeSpan.FromSeconds(10);

        return Task.FromResult(true);
    }
}
