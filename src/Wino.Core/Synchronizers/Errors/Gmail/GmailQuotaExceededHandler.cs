using System;
using System.Linq;
using System.Threading.Tasks;
using Google;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Synchronizers.Errors.Gmail;

/// <summary>
/// Handles Gmail API quota exceeded errors (HTTP 403 with quota error).
/// This is a more severe rate limit that indicates daily quota exhaustion.
/// </summary>
public class GmailQuotaExceededHandler : ISynchronizerErrorHandler
{
    private readonly ILogger _logger = Log.ForContext<GmailQuotaExceededHandler>();

    public bool CanHandle(SynchronizerErrorContext error)
    {
        if (error.Exception is GoogleApiException googleEx)
        {
            // Quota exceeded usually returns 403
            if (googleEx.HttpStatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var errorMessage = googleEx.Message?.ToLowerInvariant() ?? string.Empty;
                var errorReason = googleEx.Error?.Errors?.FirstOrDefault()?.Reason?.ToLowerInvariant() ?? string.Empty;

                return errorMessage.Contains("quota") ||
                       errorMessage.Contains("limit exceeded") ||
                       errorReason.Contains("quota") ||
                       errorReason.Contains("ratelimitexceeded") ||
                       errorReason.Contains("userlimitexceeded");
            }
        }

        return false;
    }

    public Task<bool> HandleAsync(SynchronizerErrorContext error)
    {
        _logger.Warning(error.Exception,
            "Gmail API quota exceeded for account {AccountName} ({AccountId}). Sync will be paused.",
            error.Account?.Name, error.Account?.Id);

        // Quota exceeded is more severe - treat as fatal to prevent repeated failures
        // The user will be notified and sync will resume after quota resets
        error.Severity = SynchronizerErrorSeverity.Fatal;
        error.Category = SynchronizerErrorCategory.RateLimit;

        // Suggest a very long delay - quotas typically reset daily
        error.RetryDelay = TimeSpan.FromHours(1);

        return Task.FromResult(true);
    }
}
