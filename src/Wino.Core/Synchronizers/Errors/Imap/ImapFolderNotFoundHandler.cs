using System.Threading.Tasks;
using MailKit;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Synchronizers.Errors.Imap;

/// <summary>
/// Handles IMAP folder not found errors (FolderNotFoundException).
/// Preserves cached data until folder discovery establishes absence.
/// </summary>
public class ImapFolderNotFoundHandler : ISynchronizerErrorHandler
{
    private readonly ILogger _logger = Log.ForContext<ImapFolderNotFoundHandler>();
    public bool CanHandle(SynchronizerErrorContext error)
    {
        return error.Exception is FolderNotFoundException ||
               error.ErrorCode == 404 ||
               (error.ErrorMessage?.Contains("folder not found", System.StringComparison.OrdinalIgnoreCase) ?? false) ||
               (error.ErrorMessage?.Contains("mailbox not found", System.StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public Task<bool> HandleAsync(SynchronizerErrorContext error)
    {
        _logger.Warning(error.Exception,
            "IMAP folder is unavailable for account {AccountName} ({AccountId}). Folder: {FolderName} ({FolderId}). Keeping cached data until folder discovery.",
            error.Account?.Name, error.Account?.Id, error.FolderName, error.FolderId);

        // SELECT/GetFolder failures can reflect temporary visibility or access restrictions.
        // Only a completed folder listing may reconcile local folder membership.
        error.Severity = SynchronizerErrorSeverity.Recoverable;
        error.Category = SynchronizerErrorCategory.ResourceNotFound;
        return Task.FromResult(true);
    }
}
