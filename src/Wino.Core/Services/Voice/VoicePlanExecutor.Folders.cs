#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Domain.Models.Voice;
using Wino.Core.Requests.Folder;
using Wino.Mail.Api.Contracts.Voice;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.Server;

namespace Wino.Core.Services.Voice;

/// <summary>
/// Folder steps: one folder per account, by well-known kind, provider id or name, changed through the same folder
/// requests the folder menu uses. Renaming, deleting and creating under a folder need it in exactly one account.
/// </summary>
public sealed partial class VoicePlanExecutor
{
    private async Task<StepOutcome> ResolveFolderStepAsync(VoicePlanPreviewGroup group, VoicePlanPreviewStep step, ResolveState state, CancellationToken cancellationToken)
    {
        var command = step.Command;
        var accounts = GroupAccounts(group);
        if (accounts.Count == 0)
            return StepOutcome.Drop(Translator.VoiceIssue_AccountUnnamed);

        var target = command.Folder ?? new FolderTarget();
        var named = !string.IsNullOrWhiteSpace(target.FolderId) || !string.IsNullOrWhiteSpace(target.FolderName) || ToSpecialFolderType(target.SpecialType) is not null;

        if (command.Action is VoiceAction.CreateFolder or VoiceAction.RenameFolder && string.IsNullOrWhiteSpace(target.NewName))
            return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_FolderName));

        if (command.Action == VoiceAction.CreateFolder && !named)
        {
            if (ResolveAccount(group) is not { } account)
                return StepOutcome.Drop(Translator.VoiceIssue_AccountUnnamed);

            state.FolderTargets[step.CommandIndex] = new List<VoiceFolderTarget> { new(null, account) };
            return StepOutcome.Keep(step.Detail);
        }

        if (!named)
            return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Folder));

        var found = new List<VoiceFolderTarget>();
        string? problem = null;
        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (folder, folderProblem) = await ResolveSingleFolderAsync(account, target.FolderId, target.FolderName, target.SpecialType).ConfigureAwait(false);
            if (folder is null)
                problem ??= folderProblem;
            else
                found.Add(new VoiceFolderTarget(folder, account));
        }

        if (found.Count == 0 && accounts.Count > 1 && !string.IsNullOrWhiteSpace(target.FolderName))
            return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoFolder, target.FolderName.Trim(), Translator.VoiceBubble_AllAccounts));
        if (found.Count == 0)
            return StepOutcome.Drop(problem ?? string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoFolder, target.FolderName ?? string.Empty, AccountsLabel(accounts)));

        if (command.Action is VoiceAction.RenameFolder or VoiceAction.DeleteFolder or VoiceAction.CreateFolder && found.Count > 1)
        {
            var names = found.Select(static entry => AccountTitle(entry.Account)).ToList();
            return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_FolderInAccounts, found[0].Folder?.ResolvedDisplayName, string.Join(", ", names)));
        }

        if (command.Action is VoiceAction.RenameFolder or VoiceAction.DeleteFolder
            && found[0].Folder is { } system && (system.SpecialFolderType != SpecialFolderType.Other || system.IsSystemFolder))
            return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_SystemFolder, system.ResolvedDisplayName));

        state.FolderTargets[step.CommandIndex] = found;
        return StepOutcome.Keep(found.Count == 1
            ? $"{found[0].Folder?.ResolvedDisplayName} · {AccountTitle(found[0].Account)}"
            : string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_FoldersFound, found.Count));
    }

    private async Task<StepResult> RunFolderStepAsync(StepContext context)
    {
        var command = context.Step.Command;
        if (!context.Resolution.FolderTargets.TryGetValue(context.Step.CommandIndex, out var targets) || targets.Count == 0)
            return StepResult.Failed(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Folder));

        var folders = targets.Select(static target => target.Folder).OfType<MailItemFolder>().ToList();
        var names = string.Join(", ", folders.Select(static folder => folder.ResolvedDisplayName).Distinct(StringComparer.CurrentCultureIgnoreCase));

        switch (command.Action)
        {
            case VoiceAction.MarkFolderRead:
                foreach (var folder in folders)
                    await requestDelegator.ExecuteAsync(new FolderOperationPreperationRequest(FolderOperation.MarkAllAsRead, folder)).ConfigureAwait(false);
                return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_FolderMarkedRead, names));

            case VoiceAction.EmptyFolder:
                foreach (var folder in folders)
                    await requestDelegator.ExecuteAsync(new FolderOperationPreperationRequest(FolderOperation.Empty, folder)).ConfigureAwait(false);
                return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_FolderEmptied, names));

            case VoiceAction.PinFolder or VoiceAction.UnpinFolder:
            {
                var pin = command.Action == VoiceAction.PinFolder;
                foreach (var folder in folders)
                    await requestDelegator.ExecuteAsync(new FolderOperationPreperationRequest(pin ? FolderOperation.Pin : FolderOperation.Unpin, folder)).ConfigureAwait(false);

                WeakReferenceMessenger.Default.Send(new AccountsMenuRefreshRequested(false));
                return StepResult.Done(string.Format(CultureInfo.CurrentCulture, pin ? Translator.VoiceResult_FolderPinned : Translator.VoiceResult_FolderUnpinned, names));
            }

            case VoiceAction.RenameFolder when folders.Count == 1 && command.Folder?.NewName is { Length: > 0 } newName:
                await requestDelegator.ExecuteAsync(new FolderOperationPreperationRequest(FolderOperation.Rename, folders[0], newName.Trim())).ConfigureAwait(false);
                return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Renamed, folders[0].ResolvedDisplayName, newName.Trim()));

            case VoiceAction.CreateFolder when command.Folder?.NewName is { Length: > 0 } folderName:
            {
                var target = targets[0];
                IRequestBase request = target.Folder is { } parent
                    ? new CreateSubFolderRequest(parent, folderName.Trim())
                    : new CreateRootFolderRequest(new MailItemFolder { MailAccountId = target.Account.Id }, folderName.Trim());
                await requestDelegator.ExecuteAsync(target.Account.Id, (IRequestBase[])[request]).ConfigureAwait(false);
                return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_FolderCreated, folderName.Trim()));
            }

            case VoiceAction.DeleteFolder when folders.Count == 1:
                // The plan was confirmed twice in the bubble, so the folder menu's own confirmation is not asked again.
                await requestDelegator.ExecuteAsync(folders[0].MailAccountId, (IRequestBase[])[new DeleteFolderRequest(folders[0])]).ConfigureAwait(false);
                return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Deleted, folders[0].ResolvedDisplayName));

            case VoiceAction.SyncFolder:
                foreach (var accountFolders in folders.GroupBy(static folder => folder.MailAccountId))
                {
                    WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
                    {
                        AccountId = accountFolders.Key,
                        Type = MailSynchronizationType.CustomFolders,
                        SynchronizationFolderIds = accountFolders.Select(static folder => folder.Id).ToList()
                    }));
                }

                return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_FolderSyncStarted, names));
        }

        return StepResult.Failed(Translator.VoiceIssue_Missing_Change);
    }
}
