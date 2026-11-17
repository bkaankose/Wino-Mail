#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Mail.ViewModels.Requests;

/// <summary>
/// The screen side of <see cref="IWinoRequestDelegator"/>. The backend never asks or shows anything: this class asks
/// the questions an operation needs before it starts (confirm a protected permanent delete, pick a move destination,
/// name or confirm a folder change), passes the answers on the request, and shows what the delegator returns. Every
/// ViewModel that starts these operations goes through it, so the wording and the order stay the same everywhere.
/// </summary>
public sealed class WinoRequestCoordinator(
    IWinoRequestDelegator requestDelegator,
    IMailDialogService dialogService,
    IFolderService folderService,
    IPreferencesService preferencesService,
    IShortcutPlatformService shortcutPlatformService)
{
    private static readonly WinoRequestDelegationResult Cancelled = new(0, null);

    /// <summary>Asks what the mail operation needs, queues it, and shows the error that stopped it.</summary>
    public async Task<WinoRequestDelegationResult> ExecuteMailOperationAsync(MailOperationPreperationRequest request)
    {
        var answered = await AnswerMailOperationAsync(request);
        if (answered is null) return Cancelled;

        var result = await requestDelegator.ExecuteWithResultAsync(answered);
        if (result.Error is { } error)
            ShowMailOperationError(error);

        return result;
    }

    /// <summary>
    /// Asks the questions a mail operation needs before its requests are prepared, in the order the backend used to
    /// ask them. Returns the request with the answers, or null when the user cancelled. A request that needs no
    /// question comes back unchanged.
    /// </summary>
    public async Task<MailOperationPreperationRequest?> AnswerMailOperationAsync(MailOperationPreperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mailItems = request.MailItems?.Where(item => item != null).ToList() ?? [];
        if (mailItems.Count == 0) return request;

        var action = request.Action;
        var answered = request with { MailItems = mailItems };

        if (!request.IgnoreHardDeleteProtection
            && ((action == MailOperation.SoftDelete && shortcutPlatformService.IsShiftKeyPressed()) || action == MailOperation.HardDelete))
        {
            if (preferencesService.IsHardDeleteProtectionEnabled && !await dialogService.ShowHardDeleteConfirmationAsync())
                return null;

            action = MailOperation.HardDelete;
            answered = answered with { Action = action, IgnoreHardDeleteProtection = true };
        }

        if (action == MailOperation.Move && request.MoveTargetFolder == null)
        {
            // Mails from several accounts can't share one picker; the delegator reports that as an invalid target.
            if (HasSeveralAccounts(mailItems)) return answered;

            var target = await dialogService.PickFolderAsync(mailItems[0].AssignedAccount.Id, PickFolderReason.Move, folderService);
            if (target == null) return null;

            answered = answered with { MoveTargetFolder = target };
        }

        return answered;
    }

    public void ShowMailOperationError(Exception error)
    {
        switch (error)
        {
            case UnavailableSpecialFolderException unavailableSpecialFolderException:
                dialogService.InfoBarMessage(Translator.Info_MissingFolderTitle,
                                             string.Format(Translator.Info_MissingFolderMessage, unavailableSpecialFolderException.SpecialFolderType),
                                             InfoBarMessageType.Warning,
                                             Translator.SettingConfigureSpecialFolders_Button,
                                             () =>
                                             {
                                                 _ = dialogService.HandleSystemFolderConfigurationDialogAsync(unavailableSpecialFolderException.AccountId, folderService);
                                             });
                break;
            case InvalidMoveTargetException { Reason: InvalidMoveTargetReason.NonMoveTarget }:
                dialogService.InfoBarMessage(Translator.Info_InvalidMoveTargetTitle, Translator.Info_InvalidMoveTargetMessage, InfoBarMessageType.Warning);
                break;
            case InvalidMoveTargetException { Reason: InvalidMoveTargetReason.MultipleAccounts }:
                dialogService.InfoBarMessage(Translator.Info_InvalidMoveTargetTitle, Translator.Exception_InvalidMultiAccountMoveTarget, InfoBarMessageType.Warning);
                break;
            case InvalidMoveTargetException:
                break;
            case MailOperationDecisionRequiredException decisionRequired:
                // AnswerMailOperationAsync asks every question first, so this only means a caller skipped it.
                Log.Warning("A mail operation reached the delegator without the answer {Decision}; nothing was queued.", decisionRequired.Decision);
                break;
            case NotImplementedException:
                dialogService.ShowNotSupportedMessage();
                break;
            default:
                dialogService.InfoBarMessage(Translator.Info_RequestCreationFailedTitle, error.Message, InfoBarMessageType.Error);
                break;
        }
    }

    /// <summary>Asks for the name or confirmation a folder operation needs, queues it, and shows an unsupported action.</summary>
    public async Task<WinoRequestDelegationResult> ExecuteFolderOperationAsync(FolderOperationPreperationRequest request)
    {
        if (request?.Folder is not MailItemFolder folder) return Cancelled;

        switch (request.Action)
        {
            case FolderOperation.Rename when request.NewFolderName == null:
            {
                var newFolderName = await dialogService.ShowTextInputDialogAsync(folder.FolderName, Translator.DialogMessage_RenameFolderTitle, Translator.DialogMessage_RenameFolderMessage, Translator.FolderOperation_Rename);
                if (string.IsNullOrEmpty(newFolderName)) return Cancelled;

                request = request with { NewFolderName = newFolderName };
                break;
            }
            case FolderOperation.Delete:
            {
                var deleteQuestion = string.Format(Translator.DialogMessage_DeleteAccountConfirmationMessage, folder.ResolvedDisplayName);
                if (!await dialogService.ShowConfirmationDialogAsync(deleteQuestion, Translator.FolderOperation_Delete, Translator.FolderOperation_Delete))
                    return Cancelled;

                break;
            }
            case FolderOperation.CreateSubFolder:
            {
                var subFolderName = await dialogService.ShowTextInputDialogAsync(
                    string.Empty,
                    Translator.FolderOperation_CreateSubFolder,
                    Translator.DialogMessage_RenameFolderMessage,
                    Translator.FolderOperation_CreateSubFolder);
                if (string.IsNullOrWhiteSpace(subFolderName)) return Cancelled;

                request = request with { NewFolderName = subFolderName };
                break;
            }
        }

        var result = await requestDelegator.ExecuteAsync(request);
        if (result.Error is NotImplementedException)
            dialogService.ShowNotSupportedMessage();

        return result;
    }

    /// <summary>Queues a calendar operation and tells the user when its calendar is read-only.</summary>
    public async Task<WinoRequestDelegationResult> ExecuteCalendarOperationAsync(CalendarOperationPreparationRequest request)
    {
        var result = await requestDelegator.ExecuteAsync(request);
        if (result.Error is ReadOnlyCalendarException)
            dialogService.ShowReadOnlyCalendarMessage();

        return result;
    }

    private static bool HasSeveralAccounts(List<MailCopy> mailItems)
        => mailItems.Select(a => a.AssignedAccount.Id).Distinct().Count() > 1;
}
