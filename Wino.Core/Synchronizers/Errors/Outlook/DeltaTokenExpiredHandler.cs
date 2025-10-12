using System.Threading.Tasks;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Errors;
using Wino.Core.Integration.Processors;

namespace Wino.Core.Synchronizers.Errors.Outlook;

/// <summary>
/// Handles 410 Gone errors for Outlook synchronization, which indicates that delta tokens have expired.
/// When this occurs, all local mail cache should be deleted and initial synchronization should be reset.
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
        _logger.Warning("Delta token has expired for account {AccountName} ({AccountId}). Deleting all local mail cache and resetting synchronization.", 
            error.Account.Name, error.Account.Id);

        try
        {
            // Delete all local mail cache for the account
            await _outlookChangeProcessor.DeleteUserMailCacheAsync(error.Account.Id).ConfigureAwait(false);

            // Reset the account's delta synchronization identifier
            await _outlookChangeProcessor.UpdateAccountDeltaSynchronizationIdentifierAsync(error.Account.Id, string.Empty).ConfigureAwait(false);

            // Get all folders for the account and reset their delta tokens and initial sync status
            var folders = await _outlookChangeProcessor.GetLocalFoldersAsync(error.Account.Id).ConfigureAwait(false);
            
            foreach (var folder in folders)
            {
                // Reset folder delta token
                await _outlookChangeProcessor.UpdateFolderDeltaSynchronizationIdentifierAsync(folder.Id, string.Empty).ConfigureAwait(false);
                
                // Reset initial sync completion status to force full re-sync
                await _outlookChangeProcessor.UpdateFolderInitialSyncCompletedAsync(folder.Id, false).ConfigureAwait(false);
            }

            _logger.Information("Successfully reset synchronization state for account {AccountName} ({AccountId}). Next sync will be a full re-sync.", 
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