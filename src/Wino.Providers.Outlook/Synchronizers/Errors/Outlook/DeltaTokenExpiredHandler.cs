using System.Threading.Tasks;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;

namespace Wino.Core.Synchronizers.Errors.Outlook;

/// <summary>
/// Handles 410 Gone errors for Outlook synchronization, which indicates that delta tokens have expired.
/// Reset only the expired collection. Its next full enumeration reconciles the existing cache.
/// </summary>
public class DeltaTokenExpiredHandler : ISynchronizerErrorHandler
{
    private readonly ILogger _logger = Log.ForContext<DeltaTokenExpiredHandler>();
    private readonly IOutlookChangeProcessor _outlookChangeProcessor;

    public DeltaTokenExpiredHandler(IOutlookChangeProcessor outlookChangeProcessor)
    {
        _outlookChangeProcessor = outlookChangeProcessor;
    }

    public bool CanHandle(SynchronizerErrorContext error)
    {
        // Handle 410 Gone responses which indicate delta token expiration
        return error.ErrorCode == 410 || 
               (error.Exception is ODataError oDataError && oDataError.ResponseStatusCode == 410) ||
               (error.Exception is ApiException apiException && apiException.ResponseStatusCode == 410);
    }

    public async Task<bool> HandleAsync(SynchronizerErrorContext error)
    {
        _logger.Warning("Delta token has expired for account {AccountName} ({AccountId}). Resetting the affected synchronization cursor.",
            error.Account.Name, error.Account.Id);

        try
        {
            if (error.FolderId.HasValue)
            {
                await _outlookChangeProcessor.UpdateFolderDeltaSynchronizationIdentifierAsync(error.FolderId.Value, string.Empty).ConfigureAwait(false);
            }
            else
            {
                error.Account.SynchronizationDeltaIdentifier = await _outlookChangeProcessor
                    .UpdateAccountDeltaSynchronizationIdentifierAsync(error.Account.Id, string.Empty).ConfigureAwait(false);
            }

            _logger.Information("Successfully reset synchronization state for account {AccountName} ({AccountId}). Cached mail was preserved.",
                error.Account.Name, error.Account.Id);

            return true;
        }
        catch (System.Exception ex)
        {
            _logger.Error(ex, "Failed to handle delta token expiration for account {AccountName} ({AccountId})", 
                error.Account.Name, error.Account.Id);
            
            return false;
        }
    }
}
