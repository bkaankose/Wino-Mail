using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Shell;
using Wino.Core.Domain.Models.AppLock;
using Wino.Core.Domain.Messaging.Server;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Helpers;
using Wino.Services.Interfaces;
using Wino.Services.Models.MailItem;

namespace Wino.Core.Services;

/// <summary>
/// Moves mail into another account's folder. The order is fixed by the rule that a failure may leave the mail in both
/// accounts but never in neither: the target stores an exact copy first, and only a copy the target confirmed queues
/// the source's soft delete (a move to its Trash) through the normal request pipeline. There is no journal: a mail
/// whose copy failed simply comes back in its folder.
/// </summary>
public sealed class MailTransferService(
    IWinoRequestProcessor requestProcessor,
    ISynchronizationManager synchronizationManager,
    RequestUiChangeCoordinator uiChanges,
    IMimeFileService mimeFiles,
    IFolderService folderService,
    IAccountService accountService,
    IAccountProviderFeatureService featureService,
    IProviderFeatureAuthorizationService featureAuthorization,
    IMailProviderRegistry providers,
    IAppLockService appLockService,
    IMessenger messenger) : IMailTransferService
{
    private const int RemovalFlushSize = 10;

    private readonly ILogger _logger = Log.ForContext<MailTransferService>();
    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, byte> _pendingUniqueIds = new();

    public IReadOnlyCollection<Guid> GetPendingUniqueIds() => _pendingUniqueIds.Keys.ToList();

    public bool MayReceiveMovedMail(Guid accountId)
        => synchronizationManager.GetAllSynchronizers().FirstOrDefault(synchronizer => synchronizer.Account?.Id == accountId) is not { } synchronizer
           || synchronizer.SupportsMailImport;

    public async Task<List<IMailItemFolder>> GetMoveTargetsAsync(Guid sourceAccountId, bool displayShape)
    {
        var targets = new List<IMailItemFolder>(await GetFolderTreeAsync(sourceAccountId, displayShape).ConfigureAwait(false));

        var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);

        foreach (var account in accounts.Where(account => account.Id != sourceAccountId && CanReceive(account)))
        {
            try
            {
                if (!await synchronizationManager.CanImportMailAsync(account.Id).ConfigureAwait(false))
                    continue;

                var tree = WithoutDrafts(await GetFolderTreeAsync(account.Id, displayShape).ConfigureAwait(false));
                if (tree.Count == 0)
                    continue;

                targets.Add(new MailItemFolder
                {
                    Id = account.Id,
                    MailAccountId = account.Id,
                    FolderName = account.Name,
                    IsContainer = true,
                    ChildFolders = tree
                });
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Could not list account {AccountId} as a move target.", account.Id);
            }
        }

        return targets;
    }

    private async Task<List<IMailItemFolder>> GetFolderTreeAsync(Guid accountId, bool displayShape)
        => displayShape
            ? await folderService.GetFolderStructureForDisplayAsync(accountId).ConfigureAwait(false)
            : (await folderService.GetFolderStructureForAccountAsync(accountId, false).ConfigureAwait(false)).Folders;

    // Mail can't be moved into another account's Drafts, so the folder is not offered there.
    private static List<IMailItemFolder> WithoutDrafts(List<IMailItemFolder> folders)
    {
        var kept = folders.Where(folder => folder.SpecialFolderType != SpecialFolderType.Draft || folder.ChildFolders?.Count > 0).ToList();

        foreach (var folder in kept.Where(folder => folder.ChildFolders?.Count > 0))
            folder.ChildFolders = WithoutDrafts(folder.ChildFolders);

        return kept;
    }

    private static bool CanReceive(MailAccount account)
        => account.IsMailAccessGranted && account.AttentionReason == AccountAttentionReason.None;

    public async Task ValidateMoveAsync(IReadOnlyCollection<MailCopy> mailItems, IMailItemFolder targetFolder)
        => await ResolveMoveAsync(mailItems, targetFolder).ConfigureAwait(false);

    private async Task<(MailItemFolder Folder, MailAccount Account)> ResolveMoveAsync(IReadOnlyCollection<MailCopy> mailItems, IMailItemFolder targetFolder)
    {
        if (targetFolder == null || mailItems == null || mailItems.Count == 0)
            throw new InvalidMoveTargetException(InvalidMoveTargetReason.NonMoveTarget);

        var folder = await folderService.GetFolderAsync(targetFolder.Id).ConfigureAwait(false);
        if (folder == null || !folder.IsMoveTarget)
            throw new InvalidMoveTargetException(InvalidMoveTargetReason.NonMoveTarget);

        if (mailItems.Any(item => item.AssignedAccount == null || item.AssignedFolder == null || item.AssignedAccount.Id == folder.MailAccountId))
            throw new InvalidMoveTargetException(InvalidMoveTargetReason.NonMoveTarget);

        // A draft is not mail yet, and a copy in another account's Drafts would be a draft nobody composed.
        if (folder.SpecialFolderType == SpecialFolderType.Draft || mailItems.Any(item => item.IsDraft || item.IsLocalDraft))
            throw new InvalidMoveTargetException(InvalidMoveTargetReason.CrossAccountNotSupported);

        var account = await accountService.GetAccountAsync(folder.MailAccountId).ConfigureAwait(false);
        if (account == null || !CanReceive(account) || !await synchronizationManager.CanImportMailAsync(account.Id).ConfigureAwait(false))
            throw new InvalidMoveTargetException(InvalidMoveTargetReason.CrossAccountNotSupported);

        if (providers.GetTraits(account.ProviderType).MailImportRequiresAuthorization
            && !await featureService.IsEnabledAsync(account.Id, ProviderFeature.CrossAccountMoveImport).ConfigureAwait(false))
        {
            throw new MailOperationDecisionRequiredException(MailOperationDecision.AuthorizeMailImport);
        }

        foreach (var sourceAccountId in mailItems.Select(item => item.AssignedAccount.Id).Distinct())
        {
            _ = await folderService.GetSpecialFolderByAccountIdAsync(sourceAccountId, SpecialFolderType.Deleted).ConfigureAwait(false)
                ?? throw new UnavailableSpecialFolderException(SpecialFolderType.Deleted, sourceAccountId);
        }

        return (folder, account);
    }

    public async Task StartMoveAsync(IReadOnlyCollection<MailCopy> mailItems, IMailItemFolder targetFolder)
    {
        if (appLockService.AreActionsRefused)
            throw new AppLockedException();

        var (folder, account) = await ResolveMoveAsync(mailItems, targetFolder).ConfigureAwait(false);

        var removals = await requestProcessor.PrepareRequestsAsync(new MailOperationPreperationRequest(
            MailOperation.SoftDelete,
            mailItems,
            ignoreHardDeleteProtection: true)).ConfigureAwait(false);

        var removalsByMail = removals
            .GroupBy(request => request.Item.UniqueId)
            .ToDictionary(group => group.Key, group => group.Cast<IRequestBase>().ToList());

        var transfers = mailItems
            .Where(item => removalsByMail.ContainsKey(item.UniqueId))
            .GroupBy(item => item.UniqueId)
            .Select(group => new PendingTransfer(group.First(), removalsByMail[group.Key]))
            .ToList();

        if (transfers.Count == 0)
            return;

        foreach (var transfer in transfers)
            _pendingUniqueIds[transfer.Mail.UniqueId] = 0;

        // The mail leaves its folder at once, like any move; a failed copy brings it back.
        uiChanges.ApplyRequests(transfers.SelectMany(transfer => transfer.Removals));

        _logger.Information("Moving {Count} mail(s) to folder {FolderId} of account {AccountId}.", transfers.Count, folder.Id, account.Id);

        _ = Task.Run(() => RunTransferAsync(transfers, folder, account));
    }

    private async Task RunTransferAsync(List<PendingTransfer> transfers, MailItemFolder folder, MailAccount account)
    {
        await _transferGate.WaitAsync().ConfigureAwait(false);

        var confirmed = new List<PendingTransfer>();
        var failedCount = 0;
        var keptInSourceCount = 0;
        MailImportUnavailableException unavailable = null;

        try
        {
            foreach (var transfer in transfers)
            {
                if (unavailable != null)
                {
                    Restore(transfer);
                    failedCount++;
                    continue;
                }

                try
                {
                    var mime = await GetSourceMimeAsync(transfer.Mail).ConfigureAwait(false);

                    // Registered before the copy exists, so no synchronization can announce the copy in the target
                    // or the original arriving in the source's Trash.
                    synchronizationManager.SuppressNewMailNotification(account.Id, mime.MessageId);
                    synchronizationManager.SuppressNewMailNotification(transfer.Mail.AssignedAccount.Id, mime.MessageId);

                    await synchronizationManager.ImportMailAsync(account.Id, new MailImportPackage(
                        mime,
                        folder,
                        transfer.Mail.IsRead,
                        transfer.Mail.IsFlagged,
                        transfer.Mail.CreationDate.AsStoredUtc())).ConfigureAwait(false);

                    confirmed.Add(transfer);
                }
                catch (MailImportUnavailableException ex)
                {
                    _logger.Warning(ex, "Account {AccountId} cannot receive moved mail ({Reason}).", account.Id, ex.Reason);
                    unavailable = ex;
                    Restore(transfer);
                    failedCount++;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Copying mail {UniqueId} to account {AccountId} failed; it stays in its folder.", transfer.Mail.UniqueId, account.Id);
                    Restore(transfer);
                    failedCount++;
                }

                if (confirmed.Count >= RemovalFlushSize)
                    keptInSourceCount += await QueueRemovalsAsync(confirmed).ConfigureAwait(false);
            }

            keptInSourceCount += await QueueRemovalsAsync(confirmed).ConfigureAwait(false);
        }
        finally
        {
            _transferGate.Release();
        }

        if (failedCount < transfers.Count)
        {
            messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = account.Id,
                Type = MailSynchronizationType.CustomFolders,
                SynchronizationFolderIds = [folder.Id]
            }));
        }

        await ReportAsync(transfers.Count, failedCount, keptInSourceCount, unavailable, folder, account).ConfigureAwait(false);
    }

    private async Task<MimeKit.MimeMessage> GetSourceMimeAsync(MailCopy mail)
    {
        var sourceAccountId = mail.AssignedAccount.Id;

        if (!await mimeFiles.IsMimeExistAsync(sourceAccountId, mail.FileId).ConfigureAwait(false)
            && await synchronizationManager.DownloadMimeMessageAsync(mail, sourceAccountId).ConfigureAwait(false) == null)
        {
            throw new InvalidOperationException($"The message {mail.UniqueId} could not be downloaded from its account.");
        }

        var information = await mimeFiles.GetMimeMessageInformationAsync(mail.FileId, sourceAccountId).ConfigureAwait(false);
        return information?.MimeMessage ?? throw new InvalidOperationException($"The message {mail.UniqueId} has no stored MIME.");
    }

    private void Restore(PendingTransfer transfer)
    {
        uiChanges.RevertRequests(transfer.Removals);
        uiChanges.CompleteRequests(transfer.Removals);
        _pendingUniqueIds.TryRemove(transfer.Mail.UniqueId, out _);
    }

    /// <summary>Queues the soft delete of every confirmed copy. Returns how many mails stay in their source.</summary>
    private async Task<int> QueueRemovalsAsync(List<PendingTransfer> confirmed)
    {
        if (confirmed.Count == 0)
            return 0;

        var keptInSource = 0;

        foreach (var group in confirmed.GroupBy(transfer => transfer.Mail.AssignedAccount.Id))
        {
            try
            {
                // The user's action was accepted while the window was unlocked; its second half must not be refused.
                using (appLockService.AllowActionsWhileLocked())
                {
                    await synchronizationManager.QueueRequestsAsync(
                        group.SelectMany(transfer => transfer.Removals).ToList(),
                        group.Key,
                        triggerSynchronization: true,
                        allowUndo: false).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // The queue reverted the removals, so the mail is back in its folder and also in the target.
                _logger.Error(ex, "Moved mail of account {AccountId} could not be removed from its folder.", group.Key);
                keptInSource += group.Count();
            }
        }

        foreach (var transfer in confirmed)
            _pendingUniqueIds.TryRemove(transfer.Mail.UniqueId, out _);

        confirmed.Clear();
        return keptInSource;
    }

    private async Task ReportAsync(int total, int failedCount, int keptInSourceCount, MailImportUnavailableException unavailable, MailItemFolder folder, MailAccount account)
    {
        _logger.Information("Move to account {AccountId} finished: {Moved} moved, {Failed} not copied, {Kept} copied but kept in the source.",
            account.Id, total - failedCount - keptInSourceCount, failedCount, keptInSourceCount);

        if (unavailable != null)
        {
            if (unavailable.Reason == MailImportUnavailableReason.AuthorizationRequired)
            {
                try
                {
                    await featureAuthorization.MarkReauthorizationRequiredAsync(account.Id, ProviderFeature.CrossAccountMoveImport).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Could not record that account {AccountId} needs the mail import permission again.", account.Id);
                }
            }

            messenger.Send(new InfoBarMessageRequested(
                InfoBarMessageType.Error,
                Translator.Info_CrossAccountMovePartialFailureTitle,
                string.Format(unavailable.Reason == MailImportUnavailableReason.AuthorizationRequired
                    ? Translator.Info_CrossAccountMoveAuthorizationRequiredMessage
                    : Translator.Info_CrossAccountMoveSignInRejectedMessage, account.Name)));
            return;
        }

        if (failedCount > 0)
        {
            messenger.Send(new InfoBarMessageRequested(
                InfoBarMessageType.Warning,
                Translator.Info_CrossAccountMovePartialFailureTitle,
                string.Format(Translator.Info_CrossAccountMovePartialFailureMessage, failedCount, total, account.Name)));
        }

        if (keptInSourceCount > 0)
        {
            messenger.Send(new InfoBarMessageRequested(
                InfoBarMessageType.Warning,
                Translator.Info_CrossAccountMovePartialFailureTitle,
                string.Format(Translator.Info_CrossAccountMoveSourceKeptMessage, keptInSourceCount, account.Name)));
        }
    }

    private sealed record PendingTransfer(MailCopy Mail, List<IRequestBase> Removals);
}
