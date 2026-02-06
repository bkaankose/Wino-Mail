using System;
using System.Threading.Tasks;
using Google;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;

namespace Wino.Core.Synchronizers.Errors.Gmail;

/// <summary>
/// Handles Gmail history ID expiration errors.
/// When history is no longer available, resets the account's history ID to force a full resync.
/// </summary>
public class GmailHistoryExpiredHandler : ISynchronizerErrorHandler
{
    private readonly ILogger _logger = Log.ForContext<GmailHistoryExpiredHandler>();
    private readonly IGmailChangeProcessor _gmailChangeProcessor;

    public GmailHistoryExpiredHandler(IGmailChangeProcessor gmailChangeProcessor)
    {
        _gmailChangeProcessor = gmailChangeProcessor;
    }

    public bool CanHandle(SynchronizerErrorContext error)
    {
        // Gmail returns 404 when history ID is no longer valid
        if (error.ErrorCode == 404)
        {
            var message = error.ErrorMessage?.ToLowerInvariant() ?? string.Empty;
            return message.Contains("history") || message.Contains("notfound");
        }

        if (error.Exception is GoogleApiException googleEx)
        {
            if (googleEx.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var errorMessage = googleEx.Message?.ToLowerInvariant() ?? string.Empty;
                return errorMessage.Contains("history") ||
                       errorMessage.Contains("not found") ||
                       errorMessage.Contains("starthistoryid");
            }
        }

        return false;
    }

    public async Task<bool> HandleAsync(SynchronizerErrorContext error)
    {
        _logger.Warning(error.Exception,
            "Gmail history ID expired for account {AccountName} ({AccountId}). Resetting to force full sync.",
            error.Account?.Name, error.Account?.Id);

        error.Severity = SynchronizerErrorSeverity.Recoverable;
        error.Category = SynchronizerErrorCategory.ResourceNotFound;

        // Reset the account's synchronization identifier (history ID)
        if (error.Account != null)
        {
            try
            {
                await _gmailChangeProcessor.UpdateAccountDeltaSynchronizationIdentifierAsync(
                    error.Account.Id, string.Empty).ConfigureAwait(false);

                _logger.Information("Successfully reset Gmail history ID for account {AccountName}. Next sync will be full sync.",
                    error.Account.Name);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to reset Gmail history ID for account {AccountName}",
                    error.Account.Name);
            }
        }

        return true;
    }
}
