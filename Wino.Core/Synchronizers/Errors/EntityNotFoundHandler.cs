using System;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Synchronizers.Errors;

/// <summary>
/// Handles errors that were explicitly classified as missing remote entities.
/// This avoids swallowing unrelated HTTP 404 responses that should still surface
/// to the user as real synchronization failures.
/// </summary>
public class EntityNotFoundHandler : ISynchronizerErrorHandler
{
    private readonly ILogger _logger = Log.ForContext<EntityNotFoundHandler>();
    private readonly IMailService _mailService;
    private readonly IFolderService _folderService;
    private readonly ICalendarService _calendarService;

    public EntityNotFoundHandler(IMailService mailService, IFolderService folderService, ICalendarService calendarService)
    {
        _mailService = mailService;
        _folderService = folderService;
        _calendarService = calendarService;
    }

    public bool CanHandle(SynchronizerErrorContext error)
    {
        if (!error.IsEntityNotFound) return false;
        if (error.RequestBundle == null) return false;
        return true;
    }

    public async Task<bool> HandleAsync(SynchronizerErrorContext error)
    {
        error.Severity = SynchronizerErrorSeverity.Recoverable;
        error.Category = SynchronizerErrorCategory.ResourceNotFound;

        var uiRequest = error.RequestBundle.UIChangeRequest;

        // --- Folder actions ---
        if (uiRequest is IFolderActionRequest folderAction)
        {
            _logger.Warning("Entity not found (404) for folder operation {Op} on {RemoteFolderId}. Deleting locally.",
                folderAction.Operation, folderAction.Folder.RemoteFolderId);

            try
            {
                await _folderService.DeleteFolderAsync(
                    folderAction.Folder.MailAccountId,
                    folderAction.Folder.RemoteFolderId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to delete folder locally after 404.");
            }

            return true;
        }

        // --- Individual mail actions ---
        if (uiRequest is IMailActionRequest mailAction && error.Account != null)
        {
            _logger.Warning("Entity not found (404) for mail operation {Op} on {MailId}. Deleting locally.",
                mailAction.Operation, mailAction.Item.Id);

            // Revert optimistic UI change (e.g. mark-read/flag toggle) before deleting
            error.RequestBundle.UIChangeRequest?.RevertUIChanges();

            try
            {
                await _mailService.DeleteMailAsync(
                    error.Account.Id, mailAction.Item.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to delete mail locally after 404.");
            }

            return true;
        }

        // --- Individual calendar actions ---
        if (uiRequest is ICalendarActionRequest calendarAction)
        {
            _logger.Warning("Entity not found for calendar operation {Op} on {CalendarItemId}. Deleting locally.",
                calendarAction.Operation, calendarAction.Item?.Id);

            try
            {
                if (calendarAction.Item != null)
                {
                    await _calendarService.DeleteCalendarItemAsync(calendarAction.Item.Id).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to delete calendar item locally after entity-not-found.");
            }

            return true;
        }

        // --- Batch requests (can't identify specific item) ---
        // Mark as recoverable. Next sync will clean up stale items.
        _logger.Warning("Entity not found for batch operation. Marking as recoverable.");
        return true;
    }
}
