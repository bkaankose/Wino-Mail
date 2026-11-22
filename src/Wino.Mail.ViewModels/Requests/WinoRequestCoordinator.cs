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
using Wino.Core.Domain.Models.AppLock;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Services.Interfaces;

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
    IShortcutPlatformService shortcutPlatformService,
    IMailTransferService mailTransferService,
    IProviderFeatureAuthorizationService featureAuthorization,
    IAccountService accountService,
    Links.ConnectedItemsCoordinator? connectedItems = null)
{
    private static readonly WinoRequestDelegationResult Cancelled = new(0, null);

    /// <summary>Asks what the mail operation needs, queues it, and shows the error that stopped it.</summary>
    public async Task<WinoRequestDelegationResult> ExecuteMailOperationAsync(MailOperationPreperationRequest request)
    {
        // Create task and Create event change no mail; they open the task or event editor for the mail.
        if (Links.ConnectedItemsCoordinator.IsConnectedMailOperation(request.Action))
        {
            if (connectedItems is not null)
                await connectedItems.ExecuteMailOperationAsync(request.Action, request.MailItems);

            return Cancelled;
        }

        var answered = await AnswerMailOperationAsync(request);
        if (answered is null) return Cancelled;

        var result = await requestDelegator.ExecuteWithResultAsync(answered);

        if (result.Error is MailOperationDecisionRequiredException { Decision: MailOperationDecision.AuthorizeMailImport }
            && answered.MoveTargetFolder is { } target)
        {
            if (!await AuthorizeMailImportAsync(target.MailAccountId)) return Cancelled;

            result = await requestDelegator.ExecuteWithResultAsync(answered);
        }

        if (result.Error is { } error)
            ShowMailOperationError(error);

        return result;
    }

    /// <summary>
    /// Asks for the opt-in permission an account needs before it can receive mail moved from another account, and
    /// records it once the provider granted it. False when the user declined or the sign-in failed; nothing moves then.
    /// </summary>
    private async Task<bool> AuthorizeMailImportAsync(Guid accountId)
    {
        var account = await accountService.GetAccountAsync(accountId);
        if (account == null) return false;

        if (!await dialogService.ShowConfirmationDialogAsync(
                string.Format(Translator.DialogMessage_AuthorizeMailImportMessage, account.Name),
                string.Format(Translator.DialogMessage_AuthorizeMailImportTitle, account.Name),
                Translator.Buttons_Continue))
        {
            return false;
        }

        try
        {
            await featureAuthorization.EnableAsync(accountId, ProviderFeature.CrossAccountMoveImport);
            return true;
        }
        catch (AccountSetupCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Authorizing mail import for account {AccountId} failed.", accountId);
            dialogService.InfoBarMessage(Translator.Info_RequestCreationFailedTitle, ex.Message, InfoBarMessageType.Error);
            return false;
        }
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

            // Other accounts that can receive mail are listed under their names after the account's own folders.
            var targets = await mailTransferService.GetMoveTargetsAsync(mailItems[0].AssignedAccount.Id, displayShape: false);
            var target = await dialogService.ShowMoveMailFolderDialogAsync(targets);
            if (target == null) return null;

            answered = answered with { MoveTargetFolder = target };
        }

        return answered;
    }

    public void ShowMailOperationError(Exception error)
    {
        switch (error)
        {
            case AppLockedException:
                ShowLockedMessage();
                break;
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
            case InvalidMoveTargetException { Reason: InvalidMoveTargetReason.CrossAccountNotSupported }:
                dialogService.InfoBarMessage(Translator.Info_InvalidMoveTargetTitle, Translator.Info_CrossAccountMoveNotSupportedMessage, InfoBarMessageType.Warning);
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
        else if (result.Error is AppLockedException)
            ShowLockedMessage();

        return result;
    }

    /// <summary>Queues a calendar operation and tells the user when its calendar is read-only.</summary>
    public async Task<WinoRequestDelegationResult> ExecuteCalendarOperationAsync(CalendarOperationPreparationRequest request)
    {
        var result = await requestDelegator.ExecuteAsync(request);
        if (result.Error is ReadOnlyCalendarException)
            dialogService.ShowReadOnlyCalendarMessage();
        else if (result.Error is AppLockedException)
            ShowLockedMessage();

        return result;
    }

    private void ShowLockedMessage()
        => dialogService.InfoBarMessage(Translator.AppLock_LockedTitle, Translator.AppLock_ActionDenied, InfoBarMessageType.Warning);

    private static bool HasSeveralAccounts(List<MailCopy> mailItems)
        => mailItems.Select(a => a.AssignedAccount.Id).Distinct().Count() > 1;
}
