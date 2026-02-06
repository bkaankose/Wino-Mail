using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MoreLinq;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration;
using Wino.Services.Extensions;
using IMailService = Wino.Core.Domain.Interfaces.IMailService;

namespace Wino.Core.Synchronizers.ImapSync;

/// <summary>
/// Unified IMAP synchronization strategy that automatically selects the best available method:
/// 1. QRESYNC (RFC 5162) - Best: supports quick resync with vanished messages
/// 2. CONDSTORE (RFC 4551) - Good: supports mod-seq based change tracking
/// 3. UID-based - Fallback: basic UID comparison
///
/// This consolidates the previous QResyncSynchronizer, CondstoreSynchronizer, and UidBasedSynchronizer
/// into a single, enterprise-grade implementation with proper error handling and partial failure support.
/// </summary>
public class UnifiedImapSynchronizer
{
    private readonly ILogger _logger = Log.ForContext<UnifiedImapSynchronizer>();
    private readonly IFolderService _folderService;
    private readonly IMailService _mailService;
    private readonly IImapSynchronizerErrorHandlerFactory _errorHandlerFactory;

    // Minimum summary items to Fetch for mail synchronization from IMAP.
    private readonly MessageSummaryItems MailSynchronizationFlags =
        MessageSummaryItems.Flags |
        MessageSummaryItems.UniqueId |
        MessageSummaryItems.ThreadId |
        MessageSummaryItems.EmailId |
        MessageSummaryItems.Headers |
        MessageSummaryItems.PreviewText |
        MessageSummaryItems.GMailThreadId |
        MessageSummaryItems.References |
        MessageSummaryItems.ModSeq;

    public UnifiedImapSynchronizer(
        IFolderService folderService,
        IMailService mailService,
        IImapSynchronizerErrorHandlerFactory errorHandlerFactory)
    {
        _folderService = folderService;
        _mailService = mailService;
        _errorHandlerFactory = errorHandlerFactory;
    }

    /// <summary>
    /// Determines the best synchronization strategy based on server capabilities.
    /// </summary>
    public ImapSyncStrategy DetermineSyncStrategy(IImapClient client)
    {
        if (client is WinoImapClient winoClient &&
            client.Capabilities.HasFlag(ImapCapabilities.QuickResync) &&
            winoClient.IsQResyncEnabled)
        {
            return ImapSyncStrategy.QResync;
        }

        if (client.Capabilities.HasFlag(ImapCapabilities.CondStore))
        {
            return ImapSyncStrategy.Condstore;
        }

        return ImapSyncStrategy.UidBased;
    }

    /// <summary>
    /// Main synchronization entry point. Automatically selects the best strategy.
    /// </summary>
    public async Task<FolderSyncResult> SynchronizeFolderAsync(
        IImapClient client,
        MailItemFolder folder,
        IImapSynchronizer synchronizer,
        CancellationToken cancellationToken = default)
    {
        var strategy = DetermineSyncStrategy(client);
        _logger.Debug("Using {Strategy} sync strategy for folder {FolderName}", strategy, folder.FolderName);

        try
        {
            var downloadedIds = strategy switch
            {
                ImapSyncStrategy.QResync => await SynchronizeWithQResyncAsync(client, folder, synchronizer, cancellationToken),
                ImapSyncStrategy.Condstore => await SynchronizeWithCondstoreAsync(client, folder, synchronizer, cancellationToken),
                _ => await SynchronizeWithUidBasedAsync(client, folder, synchronizer, cancellationToken)
            };

            return FolderSyncResult.Successful(folder.Id, folder.FolderName, downloadedIds.Count);
        }
        catch (FolderNotFoundException)
        {
            _logger.Warning("Folder {FolderName} not found on server, deleting locally", folder.FolderName);
            await _folderService.DeleteFolderAsync(folder.MailAccountId, folder.RemoteFolderId).ConfigureAwait(false);

            return FolderSyncResult.Skipped(folder.Id, folder.FolderName, "Folder not found on server");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var errorContext = new SynchronizerErrorContext
            {
                ErrorMessage = ex.Message,
                Exception = ex,
                FolderId = folder.Id,
                FolderName = folder.FolderName,
                OperationType = "ImapFolderSync"
            };

            var handled = await _errorHandlerFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);

            if (errorContext.CanContinueSync)
            {
                _logger.Warning(ex, "Folder {FolderName} sync failed with recoverable error", folder.FolderName);
                return FolderSyncResult.Failed(folder.Id, folder.FolderName, errorContext);
            }

            _logger.Error(ex, "Folder {FolderName} sync failed with fatal error", folder.FolderName);
            throw;
        }
    }

    #region QRESYNC Strategy

    private async Task<List<string>> SynchronizeWithQResyncAsync(
        IImapClient client,
        MailItemFolder folder,
        IImapSynchronizer synchronizer,
        CancellationToken cancellationToken)
    {
        var downloadedMessageIds = new List<string>();

        if (client is not WinoImapClient winoClient)
            throw new InvalidOperationException("QRESYNC requires WinoImapClient");

        IMailFolder remoteFolder = null;

        try
        {
            remoteFolder = await client.GetFolderAsync(folder.RemoteFolderId, cancellationToken).ConfigureAwait(false);

            var localHighestModSeq = (ulong)Math.Max(folder.HighestModeSeq, 1);
            var allUids = await _folderService.GetKnownUidsForFolderAsync(folder.Id);
            var allUniqueIds = allUids.Select(a => new UniqueId(a)).ToList();

            // Subscribe to events before opening
            remoteFolder.MessagesVanished += (s, e) => HandleMessagesVanished(folder, e.UniqueIds);
            remoteFolder.MessageFlagsChanged += (s, e) => HandleMessageFlagsChanged(folder, e.UniqueId, e.Flags);

            // Open with QRESYNC parameters
            await remoteFolder.OpenAsync(FolderAccess.ReadOnly, folder.UidValidity, localHighestModSeq, allUniqueIds, cancellationToken).ConfigureAwait(false);

            // Get changed UIDs
            var changedUids = await remoteFolder.SearchAsync(SearchQuery.ChangedSince(localHighestModSeq), cancellationToken).ConfigureAwait(false);

            downloadedMessageIds = await ProcessChangedUidsAsync(synchronizer, remoteFolder, folder, changedUids, cancellationToken).ConfigureAwait(false);

            // Update folder tracking
            folder.HighestModeSeq = unchecked((long)remoteFolder.HighestModSeq);
            folder.UidValidity = remoteFolder.UidValidity;

            // Handle deletions
            await HandleDeletedMessagesAsync(folder, remoteFolder, cancellationToken).ConfigureAwait(false);

            await _folderService.UpdateFolderAsync(folder).ConfigureAwait(false);
        }
        finally
        {
            if (remoteFolder?.IsOpen == true && !cancellationToken.IsCancellationRequested)
            {
                await remoteFolder.CloseAsync().ConfigureAwait(false);
            }
        }

        return downloadedMessageIds;
    }

    #endregion

    #region CONDSTORE Strategy

    private async Task<List<string>> SynchronizeWithCondstoreAsync(
        IImapClient client,
        MailItemFolder folder,
        IImapSynchronizer synchronizer,
        CancellationToken cancellationToken)
    {
        var downloadedMessageIds = new List<string>();
        IMailFolder remoteFolder = null;

        try
        {
            remoteFolder = await client.GetFolderAsync(folder.RemoteFolderId, cancellationToken).ConfigureAwait(false);
            await remoteFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

            var localHighestModSeq = (ulong)folder.HighestModeSeq;
            bool isInitialSync = localHighestModSeq == 0;

            if (remoteFolder.HighestModSeq > localHighestModSeq || isInitialSync)
            {
                IList<UniqueId> changedUids;

                // Use SORT if available for better ordering
                if (client.Capabilities.HasFlag(ImapCapabilities.Sort))
                {
                    changedUids = await remoteFolder.SortAsync(
                        SearchQuery.ChangedSince(Math.Max(localHighestModSeq, 1)),
                        [OrderBy.ReverseDate],
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    changedUids = await remoteFolder.SearchAsync(
                        SearchQuery.ChangedSince(Math.Max(localHighestModSeq, 1)),
                        cancellationToken).ConfigureAwait(false);
                }

                // For initial sync, limit the number of messages
                if (isInitialSync)
                {
                    changedUids = changedUids
                        .OrderByDescending(a => a.Id)
                        .Take((int)synchronizer.InitialMessageDownloadCountPerFolder)
                        .ToList();
                }

                downloadedMessageIds = await ProcessChangedUidsAsync(synchronizer, remoteFolder, folder, changedUids, cancellationToken).ConfigureAwait(false);

                folder.HighestModeSeq = unchecked((long)remoteFolder.HighestModSeq);
                await _folderService.UpdateFolderAsync(folder).ConfigureAwait(false);
            }

            await HandleDeletedMessagesAsync(folder, remoteFolder, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (remoteFolder?.IsOpen == true && !cancellationToken.IsCancellationRequested)
            {
                await remoteFolder.CloseAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        return downloadedMessageIds;
    }

    #endregion

    #region UID-Based Strategy (Fallback)

    private async Task<List<string>> SynchronizeWithUidBasedAsync(
        IImapClient client,
        MailItemFolder folder,
        IImapSynchronizer synchronizer,
        CancellationToken cancellationToken)
    {
        var downloadedMessageIds = new List<string>();
        IMailFolder remoteFolder = null;

        try
        {
            remoteFolder = await client.GetFolderAsync(folder.RemoteFolderId, cancellationToken).ConfigureAwait(false);
            await remoteFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

            // Get all remote UIDs and take the most recent ones
            var remoteUids = await remoteFolder.SearchAsync(SearchQuery.All, cancellationToken).ConfigureAwait(false);
            var limitedUids = remoteUids
                .OrderByDescending(a => a.Id)
                .Take((int)synchronizer.InitialMessageDownloadCountPerFolder)
                .ToList();

            downloadedMessageIds = await ProcessChangedUidsAsync(synchronizer, remoteFolder, folder, limitedUids, cancellationToken).ConfigureAwait(false);

            await HandleDeletedMessagesAsync(folder, remoteFolder, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (remoteFolder?.IsOpen == true && !cancellationToken.IsCancellationRequested)
            {
                await remoteFolder.CloseAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        return downloadedMessageIds;
    }

    #endregion

    #region Shared Processing Methods

    private async Task<List<string>> ProcessChangedUidsAsync(
        IImapSynchronizer synchronizer,
        IMailFolder remoteFolder,
        MailItemFolder localFolder,
        IList<UniqueId> changedUids,
        CancellationToken cancellationToken)
    {
        var downloadedMessageIds = new List<string>();

        if (changedUids == null || changedUids.Count == 0)
            return downloadedMessageIds;

        // Get existing mails to determine what's new vs. updated
        var existingMails = await _mailService.GetExistingMailsAsync(localFolder.Id, changedUids).ConfigureAwait(false);
        var existingMailUids = existingMails.Select(m => MailkitClientExtensions.ResolveUidStruct(m.Id)).ToArray();

        var newMessageUids = changedUids.Except(existingMailUids).ToList();

        // Update flags for existing mails
        if (existingMailUids.Any())
        {
            var existingFlagData = await remoteFolder.FetchAsync(existingMailUids, MessageSummaryItems.Flags | MessageSummaryItems.UniqueId, cancellationToken).ConfigureAwait(false);

            foreach (var update in existingFlagData)
            {
                if (update.UniqueId == UniqueId.Invalid || update.Flags == null) continue;

                var existingMail = existingMails.FirstOrDefault(m => MailkitClientExtensions.ResolveUidStruct(m.Id).Id == update.UniqueId.Id);
                if (existingMail != null)
                {
                    await UpdateMailFlagsAsync(existingMail, update.Flags.Value).ConfigureAwait(false);
                }
            }
        }

        // Download new messages in batches
        var batches = newMessageUids.Batch(50);
        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchList = batch.ToList();
            downloadedMessageIds.AddRange(batchList.Select(uid => MailkitClientExtensions.CreateUid(localFolder.Id, uid.Id)));

            await DownloadMessagesAsync(synchronizer, remoteFolder, localFolder, new UniqueIdSet(batchList, SortOrder.Ascending), cancellationToken).ConfigureAwait(false);
        }

        return downloadedMessageIds;
    }

    private async Task DownloadMessagesAsync(
        IImapSynchronizer synchronizer,
        IMailFolder folder,
        MailItemFolder localFolder,
        UniqueIdSet uniqueIdSet,
        CancellationToken cancellationToken)
    {
        var summaries = await folder.FetchAsync(uniqueIdSet, MailSynchronizationFlags, cancellationToken).ConfigureAwait(false);

        foreach (var summary in summaries)
        {
            try
            {
                var mimeMessage = await folder.GetMessageAsync(summary.UniqueId, cancellationToken).ConfigureAwait(false);
                var creationPackage = new ImapMessageCreationPackage(summary, mimeMessage);
                var mailPackages = await synchronizer.CreateNewMailPackagesAsync(creationPackage, localFolder, cancellationToken).ConfigureAwait(false);

                if (mailPackages != null)
                {
                    foreach (var package in mailPackages)
                    {
                        if (package != null)
                        {
                            await _mailService.CreateMailAsync(localFolder.MailAccountId, package).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to download message {UniqueId} in folder {FolderName}", summary.UniqueId, localFolder.FolderName);
                // Continue with other messages
            }
        }
    }

    private async Task HandleDeletedMessagesAsync(MailItemFolder localFolder, IMailFolder remoteFolder, CancellationToken cancellationToken)
    {
        var allLocalUids = (await _folderService.GetKnownUidsForFolderAsync(localFolder.Id)).Select(a => new UniqueId(a)).ToList();

        if (allLocalUids.Count == 0) return;

        var remoteAllUids = await remoteFolder.SearchAsync(SearchQuery.All, cancellationToken).ConfigureAwait(false);
        var deletedUids = allLocalUids.Except(remoteAllUids).ToList();

        foreach (var deletedUid in deletedUids)
        {
            var localMailCopyId = MailkitClientExtensions.CreateUid(localFolder.Id, deletedUid.Id);
            await _mailService.DeleteMailAsync(localFolder.MailAccountId, localMailCopyId).ConfigureAwait(false);
        }
    }

    private async Task UpdateMailFlagsAsync(MailCopy mailCopy, MessageFlags flags)
    {
        var isFlagged = MailkitClientExtensions.GetIsFlagged(flags);
        var isRead = MailkitClientExtensions.GetIsRead(flags);

        if (isFlagged != mailCopy.IsFlagged)
        {
            await _mailService.ChangeFlagStatusAsync(mailCopy.Id, isFlagged).ConfigureAwait(false);
        }

        if (isRead != mailCopy.IsRead)
        {
            await _mailService.ChangeReadStatusAsync(mailCopy.Id, isRead).ConfigureAwait(false);
        }
    }

    private void HandleMessagesVanished(MailItemFolder folder, IList<UniqueId> uniqueIds)
    {
        // Fire and forget - these are event handlers
        _ = Task.Run(async () =>
        {
            foreach (var uniqueId in uniqueIds)
            {
                var localMailCopyId = MailkitClientExtensions.CreateUid(folder.Id, uniqueId.Id);
                await _mailService.DeleteMailAsync(folder.MailAccountId, localMailCopyId).ConfigureAwait(false);
            }
        });
    }

    private void HandleMessageFlagsChanged(MailItemFolder folder, UniqueId? uniqueId, MessageFlags flags)
    {
        if (uniqueId == null) return;

        _ = Task.Run(async () =>
        {
            var localMailCopyId = MailkitClientExtensions.CreateUid(folder.Id, uniqueId.Value.Id);
            var isFlagged = MailkitClientExtensions.GetIsFlagged(flags);
            var isRead = MailkitClientExtensions.GetIsRead(flags);

            await _mailService.ChangeReadStatusAsync(localMailCopyId, isRead).ConfigureAwait(false);
            await _mailService.ChangeFlagStatusAsync(localMailCopyId, isFlagged).ConfigureAwait(false);
        });
    }

    #endregion
}

/// <summary>
/// IMAP synchronization strategy enumeration.
/// </summary>
public enum ImapSyncStrategy
{
    /// <summary>
    /// RFC 5162 Quick Resync - supports vanished messages and efficient delta sync.
    /// </summary>
    QResync,

    /// <summary>
    /// RFC 4551 Conditional Store - supports mod-seq based change tracking.
    /// </summary>
    Condstore,

    /// <summary>
    /// Basic UID-based synchronization - fallback for servers without advanced features.
    /// </summary>
    UidBased
}
