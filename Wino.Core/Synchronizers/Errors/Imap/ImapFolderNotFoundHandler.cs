using System.Threading.Tasks;
using MailKit;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;

namespace Wino.Core.Synchronizers.Errors.Imap;

/// <summary>
/// Handles IMAP folder not found errors (FolderNotFoundException).
/// Deletes the folder locally and allows sync to continue with other folders.
/// </summary>
public class ImapFolderNotFoundHandler : ISynchronizerErrorHandler
{
    private readonly ILogger _logger = Log.ForContext<ImapFolderNotFoundHandler>();
    private readonly IImapChangeProcessor _imapChangeProcessor;

    public ImapFolderNotFoundHandler(IImapChangeProcessor imapChangeProcessor)
    {
        _imapChangeProcessor = imapChangeProcessor;
    }

    public bool CanHandle(SynchronizerErrorContext error)
    {
        return error.Exception is FolderNotFoundException ||
               error.ErrorCode == 404 ||
               (error.ErrorMessage?.Contains("folder not found", System.StringComparison.OrdinalIgnoreCase) ?? false) ||
               (error.ErrorMessage?.Contains("mailbox not found", System.StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public async Task<bool> HandleAsync(SynchronizerErrorContext error)
    {
        _logger.Warning(error.Exception,
            "IMAP folder not found for account {AccountName} ({AccountId}). Folder: {FolderName} ({FolderId}). Removing locally.",
            error.Account?.Name, error.Account?.Id, error.FolderName, error.FolderId);

        // Mark as recoverable - sync can continue with other folders
        error.Severity = SynchronizerErrorSeverity.Recoverable;
        error.Category = SynchronizerErrorCategory.ResourceNotFound;

        // Try to delete the folder locally if we have the folder ID
        if (error.FolderId.HasValue && error.Account != null)
        {
            try
            {
                // Get the folder's remote ID from the exception if available
                var remoteId = error.Exception is FolderNotFoundException fnf ? fnf.FolderName : null;

                if (!string.IsNullOrEmpty(remoteId))
                {
                    await _imapChangeProcessor.DeleteFolderAsync(error.Account.Id, remoteId).ConfigureAwait(false);
                    _logger.Information("Successfully deleted local folder {FolderName} after server deletion.",
                        error.FolderName);
                }
            }
            catch (System.Exception ex)
            {
                _logger.Warning(ex, "Failed to delete local folder {FolderName} ({FolderId})",
                    error.FolderName, error.FolderId);
            }
        }

        return true;
    }
}
