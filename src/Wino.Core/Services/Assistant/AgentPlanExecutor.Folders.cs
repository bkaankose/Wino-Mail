#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Agent.Runtime.Plan;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests.Folder;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.Server;

namespace Wino.Core.Services.Assistant;

/// <summary>
/// Folder steps: one folder named by id, changed through the same folder requests the folder menu uses. A new folder
/// goes under the named parent, else at the root of the named account.
/// </summary>
public sealed partial class AgentPlanExecutor
{
    private async Task<StepOutcome> RunFolderStepAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        var args = step.Args as StepArgs.FolderArgs;
        if (step.Action == AgentAction.CreateFolder)
            return await CreateFolderAsync(step, args, run).ConfigureAwait(false);

        if (step.Target is not StepTarget.Folder target)
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Folder));

        ct.ThrowIfCancellationRequested();
        var folder = await folderService.GetFolderAsync(target.FolderId).ConfigureAwait(false);
        if (folder is null)
            return new StepOutcome(step.N, StepStatus.NothingMatched, [], string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoFolder, Gloss(step), Translator.VoiceBubble_AllAccounts));

        var name = Plain(folder.ResolvedDisplayName ?? string.Empty);
        var touched = new[] { ItemRef.Folder(folder.MailAccountId, folder.Id) };
        if (step.Action is AgentAction.RenameFolder or AgentAction.DeleteFolder && (folder.SpecialFolderType != SpecialFolderType.Other || folder.IsSystemFolder))
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_SystemFolder, name));

        switch (step.Action)
        {
            case AgentAction.MarkFolderRead:
                await requestDelegator.ExecuteAsync(new FolderOperationPreperationRequest(FolderOperation.MarkAllAsRead, folder)).ConfigureAwait(false);
                return Done(step, touched, string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_FolderMarkedRead, name));

            case AgentAction.EmptyFolder:
                await requestDelegator.ExecuteAsync(new FolderOperationPreperationRequest(FolderOperation.Empty, folder)).ConfigureAwait(false);
                return Done(step, touched, string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_FolderEmptied, name));

            case AgentAction.PinFolder or AgentAction.UnpinFolder:
            {
                var pin = step.Action == AgentAction.PinFolder;
                await requestDelegator.ExecuteAsync(new FolderOperationPreperationRequest(pin ? FolderOperation.Pin : FolderOperation.Unpin, folder)).ConfigureAwait(false);
                WeakReferenceMessenger.Default.Send(new AccountsMenuRefreshRequested(false));
                return Done(step, touched, string.Format(CultureInfo.CurrentCulture, pin ? Translator.VoiceResult_FolderPinned : Translator.VoiceResult_FolderUnpinned, name));
            }

            case AgentAction.RenameFolder:
            {
                var newName = args?.NewName?.Trim() ?? args?.Name?.Trim();
                if (string.IsNullOrWhiteSpace(newName))
                    return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_FolderName));

                await requestDelegator.ExecuteAsync(new FolderOperationPreperationRequest(FolderOperation.Rename, folder, newName)).ConfigureAwait(false);
                return Done(step, touched, string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Renamed, name, Plain(newName)));
            }

            case AgentAction.DeleteFolder:
                // The plan was confirmed in the bubble, so the folder menu's own confirmation is not asked again.
                await requestDelegator.ExecuteAsync(folder.MailAccountId, (IRequestBase[])[new DeleteFolderRequest(folder)]).ConfigureAwait(false);
                return Done(step, touched, string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Deleted, name));

            case AgentAction.SyncFolder:
                WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
                {
                    AccountId = folder.MailAccountId,
                    Type = MailSynchronizationType.CustomFolders,
                    SynchronizationFolderIds = [folder.Id]
                }));
                return Done(step, touched, string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_FolderSyncStarted, name));
        }

        return Failed(step, Translator.VoiceIssue_Missing_Change);
    }

    private async Task<StepOutcome> CreateFolderAsync(ResolvedStep step, StepArgs.FolderArgs? args, PlanRun run)
    {
        var folderName = args?.Name?.Trim() ?? args?.NewName?.Trim();
        if (string.IsNullOrWhiteSpace(folderName))
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_FolderName));

        MailItemFolder? parent = null;
        if (args?.ParentFolderId is Guid parentId)
            parent = await folderService.GetFolderAsync(parentId).ConfigureAwait(false);

        var accounts = await AccountsAsync(run).ConfigureAwait(false);
        var account = await AccountAsync(run, parent?.MailAccountId ?? args?.AccountId).ConfigureAwait(false) ?? (accounts.Count == 1 ? accounts[0] : null);
        if (account is null)
            return Failed(step, Translator.VoiceIssue_AccountUnnamed);

        IRequestBase request = parent is not null
            ? new CreateSubFolderRequest(parent, folderName)
            : new CreateRootFolderRequest(new MailItemFolder { MailAccountId = account.Id }, folderName);
        await requestDelegator.ExecuteAsync(account.Id, (IRequestBase[])[request]).ConfigureAwait(false);

        var touched = parent is null ? [] : new[] { ItemRef.Folder(parent.MailAccountId, parent.Id) };
        return Done(step, touched, string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_FolderCreated, Plain(folderName)));
    }
}
