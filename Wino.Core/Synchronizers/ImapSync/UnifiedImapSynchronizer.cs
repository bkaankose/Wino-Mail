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
using Wino.Core.Domain.Extensions;
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
/// 3. UID-based delta - Fallback: tracks UIDNEXT/high-water UID without sequence-number persistence
/// </summary>
public class UnifiedImapSynchronizer
{
    private static readonly TimeSpan UidReconcileInterval = TimeSpan.FromHours(12);
    private const int NewMessageFetchBatchSize = 50;
    private const int ExistingMessageFlagFetchBatchSize = 250;

    private readonly ILogger _logger = Log.ForContext<UnifiedImapSynchronizer>();
    private readonly IFolderService _folderService;
    private readonly IMailService _mailService;
    private readonly IImapSynchronizerErrorHandlerFactory _errorHandlerFactory;

    // Metadata-first synchronization flags: no full MIME body download.
    private readonly MessageSummaryItems _mailSynchronizationFlags =
        MessageSummaryItems.Flags |
        MessageSummaryItems.UniqueId |
        MessageSummaryItems.InternalDate |
        MessageSummaryItems.Envelope |
        MessageSummaryItems.Headers |
        MessageSummaryItems.PreviewText |
        MessageSummaryItems.GMailThreadId |
        MessageSummaryItems.References |
        MessageSummaryItems.ModSeq |
        MessageSummaryItems.BodyStructure;
    private readonly MessageSummaryItems _existingMailSynchronizationFlags =
        MessageSummaryItems.Flags |
        MessageSummaryItems.UniqueId;

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
    /// Determines the best synchronization strategy based on server capabilities and known quirks.
    /// </summary>
    public ImapSyncStrategy DetermineSyncStrategy(IImapClient client, string serverHost)
    {
        var capabilities = client.Capabilities;
        var isQResyncEnabled = client is WinoImapClient winoClient && winoClient.IsQResyncEnabled;

        return DetermineSyncStrategy(capabilities, isQResyncEnabled, serverHost);
    }

    public ImapSyncStrategy DetermineSyncStrategy(ImapCapabilities capabilities, bool isQResyncEnabled, string serverHost = null)
    {
        var quirks = ImapServerQuirks.Resolve(serverHost);

        if (!quirks.DisableQResync && capabilities.HasFlag(ImapCapabilities.QuickResync) && isQResyncEnabled)
            return ImapSyncStrategy.QResync;

        if (!quirks.DisableCondstore && capabilities.HasFlag(ImapCapabilities.CondStore))
            return ImapSyncStrategy.Condstore;

        return ImapSyncStrategy.UidBased;
    }

    /// <summary>
    /// Main synchronization entry point. Automatically selects the best strategy.
    /// </summary>
    public async Task<FolderSyncResult> SynchronizeFolderAsync(
        IImapClient client,
        MailItemFolder folder,
        IImapSynchronizer synchronizer,
        string serverHost,
        CancellationToken cancellationToken = default)
    {
        var strategy = DetermineSyncStrategy(client, serverHost);
        _logger.Debug("Using {Strategy} sync strategy for folder {FolderName}", strategy, folder.FolderName);

        var originalHighestModeSeq = folder.HighestModeSeq;
        var originalUidValidity = folder.UidValidity;
        var originalHighestKnownUid = folder.HighestKnownUid;
        var originalLastUidReconcileUtc = folder.LastUidReconcileUtc;

        try
        {
            var downloadedIds = strategy switch
            {
                ImapSyncStrategy.QResync => await SynchronizeWithQResyncAsync(client, folder, synchronizer, cancellationToken).ConfigureAwait(false),
                ImapSyncStrategy.Condstore => await SynchronizeWithCondstoreAsync(client, folder, synchronizer, cancellationToken).ConfigureAwait(false),
                _ => await SynchronizeWithUidDeltaAsync(client, folder, synchronizer, cancellationToken).ConfigureAwait(false)
            };

            bool highestModeSeqChanged = folder.HighestModeSeq != originalHighestModeSeq;
            bool requiresFullFolderUpdate =
                folder.UidValidity != originalUidValidity
                || folder.HighestKnownUid != originalHighestKnownUid
                || folder.LastUidReconcileUtc != originalLastUidReconcileUtc;

            if (requiresFullFolderUpdate)
            {
                // Persist all sync-state fields in one write when any non-mod-seq token changed.
                await _folderService.UpdateFolderAsync(folder).ConfigureAwait(false);
            }
            else if (highestModeSeqChanged)
            {
                // Avoid full-folder write when only mod-seq changed.
                await _folderService.UpdateFolderHighestModeSeqAsync(folder.Id, folder.HighestModeSeq).ConfigureAwait(false);
            }

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

            _ = await _errorHandlerFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);

            if (errorContext.CanContinueSync)
            {
                _logger.Warning(ex, "Folder {FolderName} sync failed with recoverable error", folder.FolderName);
                return FolderSyncResult.Failed(folder.Id, folder.FolderName, errorContext);
            }

            _logger.Error(ex, "Folder {FolderName} sync failed with fatal error", folder.FolderName);
            throw;
        }
    }

    /// <summary>
    /// Metadata-only message download helper used by IMAP online search.
    /// </summary>
    public async Task<List<string>> DownloadMessagesByUidsAsync(
        IImapClient client,
        IMailFolder remoteFolder,
        MailItemFolder localFolder,
        IList<UniqueId> uids,
        IImapSynchronizer synchronizer,
        CancellationToken cancellationToken = default)
    {
        if (uids == null || uids.Count == 0)
            return [];

        if (!remoteFolder.IsOpen)
            await remoteFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

        var downloadedMessageIds = new List<string>();

        foreach (var batch in uids.Distinct().OrderBy(a => a.Id).Batch(ExistingMessageFlagFetchBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchUids = batch.ToList();
            var existingMails = await _mailService.GetExistingMailsAsync(localFolder.Id, batchUids).ConfigureAwait(false);
            var existingByUid = CreateExistingMailLookup(existingMails);
            var existingUids = batchUids.Where(uid => existingByUid.ContainsKey(uid.Id)).ToList();
            var newUids = batchUids.Where(uid => !existingByUid.ContainsKey(uid.Id)).ToList();

            if (existingUids.Count > 0)
            {
                var existingSummaryBatch = await remoteFolder
                    .FetchAsync(new UniqueIdSet(existingUids, SortOrder.Ascending), _existingMailSynchronizationFlags, cancellationToken)
                    .ConfigureAwait(false);

                await ApplySummaryFlagUpdatesAsync(existingByUid, existingSummaryBatch).ConfigureAwait(false);
            }

            foreach (var newBatch in newUids.Batch(NewMessageFetchBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var newSummaryBatch = await remoteFolder
                    .FetchAsync(new UniqueIdSet(newBatch.ToList(), SortOrder.Ascending), _mailSynchronizationFlags, cancellationToken)
                    .ConfigureAwait(false);

                downloadedMessageIds.AddRange(await ProcessSummariesCoreAsync(synchronizer, localFolder, newSummaryBatch, existingByUid, cancellationToken).ConfigureAwait(false));
            }
        }

        UpdateHighestKnownUid(localFolder, remoteFolder, uids.Select(a => a.Id));
        return downloadedMessageIds;
    }

    #region Strategy Implementations

    private async Task<List<string>> SynchronizeWithQResyncAsync(
        IImapClient client,
        MailItemFolder folder,
        IImapSynchronizer synchronizer,
        CancellationToken cancellationToken)
    {
        if (client is not WinoImapClient)
            throw new InvalidOperationException("QRESYNC requires WinoImapClient.");

        var downloadedMessageIds = new List<string>();
        IMailFolder remoteFolder = null;

        var vanishedUids = new List<UniqueId>();
        var changedFlags = new Dictionary<uint, MessageFlags>();

        void OnMessagesVanished(object sender, MessagesVanishedEventArgs args)
        {
            lock (vanishedUids)
            {
                vanishedUids.AddRange(args.UniqueIds);
            }
        }

        void OnMessageFlagsChanged(object sender, MessageFlagsChangedEventArgs args)
        {
            if (args.UniqueId is not UniqueId uniqueId)
                return;

            lock (changedFlags)
            {
                changedFlags[uniqueId.Id] = args.Flags;
            }
        }

        try
        {
            remoteFolder = await client.GetFolderAsync(folder.RemoteFolderId, cancellationToken).ConfigureAwait(false);

            // Open once to validate UIDVALIDITY and reset local state if needed.
            await remoteFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            await EnsureUidValidityStateAsync(folder, remoteFolder).ConfigureAwait(false);
            await remoteFolder.CloseAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            var knownUids = await _folderService.GetKnownUidsForFolderAsync(folder.Id).ConfigureAwait(false);
            var knownUidStructs = knownUids.Select(a => new UniqueId(a)).ToList();
            var localHighestModSeq = (ulong)Math.Max(folder.HighestModeSeq, 1);

            remoteFolder.MessagesVanished += OnMessagesVanished;
            remoteFolder.MessageFlagsChanged += OnMessageFlagsChanged;

            await remoteFolder
                .OpenAsync(FolderAccess.ReadOnly, folder.UidValidity, localHighestModSeq, knownUidStructs, cancellationToken)
                .ConfigureAwait(false);

            IList<UniqueId> changedUids;

            if (folder.HighestModeSeq == 0)
            {
                changedUids = await remoteFolder
                    .SearchAsync(BuildInitialSyncQuery(synchronizer), cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                changedUids = await remoteFolder
                    .SearchAsync(SearchQuery.ChangedSince(localHighestModSeq), cancellationToken)
                    .ConfigureAwait(false);
            }

            var existingMails = await _mailService.GetExistingMailsAsync(folder.Id, changedUids).ConfigureAwait(false);
            var existingByUid = CreateExistingMailLookup(existingMails);
            var newOrUnknownUids = changedUids.Where(uid => !existingByUid.ContainsKey(uid.Id)).ToList();
            var existingUidsWithoutFlagEvents = changedUids
                .Where(uid => existingByUid.ContainsKey(uid.Id) && !changedFlags.ContainsKey(uid.Id))
                .ToList();

            if (existingUidsWithoutFlagEvents.Count > 0)
            {
                var missingEventSummaries = await remoteFolder
                    .FetchAsync(new UniqueIdSet(existingUidsWithoutFlagEvents, SortOrder.Ascending), _existingMailSynchronizationFlags, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var summary in missingEventSummaries)
                {
                    if (summary.UniqueId != UniqueId.Invalid && summary.Flags != null)
                    {
                        changedFlags[summary.UniqueId.Id] = summary.Flags.Value;
                    }
                }
            }

            downloadedMessageIds = await DownloadMessagesByUidsAsync(client, remoteFolder, folder, newOrUnknownUids, synchronizer, cancellationToken).ConfigureAwait(false);

            folder.HighestModeSeq = unchecked((long)remoteFolder.HighestModSeq);

            await ApplyFlagChangesAsync(folder, changedFlags).ConfigureAwait(false);
            await ApplyDeletedUidsAsync(folder, vanishedUids).ConfigureAwait(false);

            if (ShouldRunUidReconcile(folder))
            {
                await ReconcileDeletedMessagesAsync(folder, remoteFolder, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (remoteFolder != null)
            {
                remoteFolder.MessagesVanished -= OnMessagesVanished;
                remoteFolder.MessageFlagsChanged -= OnMessageFlagsChanged;

                if (remoteFolder.IsOpen && !cancellationToken.IsCancellationRequested)
                {
                    await remoteFolder.CloseAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return downloadedMessageIds;
    }

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

            await EnsureUidValidityStateAsync(folder, remoteFolder).ConfigureAwait(false);

            var localHighestModSeq = (ulong)Math.Max(folder.HighestModeSeq, 1);
            bool isInitialSync = folder.HighestModeSeq == 0;

            if (remoteFolder.HighestModSeq > localHighestModSeq || isInitialSync)
            {
                IList<UniqueId> changedUids;

                if (isInitialSync)
                {
                    changedUids = await remoteFolder
                        .SearchAsync(BuildInitialSyncQuery(synchronizer), cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    if (client.Capabilities.HasFlag(ImapCapabilities.Sort))
                    {
                        changedUids = await remoteFolder
                            .SortAsync(SearchQuery.ChangedSince(localHighestModSeq), [OrderBy.ReverseDate], cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        changedUids = await remoteFolder
                            .SearchAsync(SearchQuery.ChangedSince(localHighestModSeq), cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                downloadedMessageIds = await DownloadMessagesByUidsAsync(client, remoteFolder, folder, changedUids, synchronizer, cancellationToken).ConfigureAwait(false);
                folder.HighestModeSeq = unchecked((long)remoteFolder.HighestModSeq);
            }

            if (ShouldRunUidReconcile(folder))
            {
                await ReconcileDeletedMessagesAsync(folder, remoteFolder, cancellationToken).ConfigureAwait(false);
            }
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

    private async Task<List<string>> SynchronizeWithUidDeltaAsync(
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

            await EnsureUidValidityStateAsync(folder, remoteFolder).ConfigureAwait(false);

            if (folder.HighestKnownUid == 0)
            {
                var initialUids = await remoteFolder
                    .SearchAsync(BuildInitialSyncQuery(synchronizer), cancellationToken)
                    .ConfigureAwait(false);

                downloadedMessageIds = await DownloadMessagesByUidsAsync(client, remoteFolder, folder, initialUids, synchronizer, cancellationToken).ConfigureAwait(false);
                UpdateHighestKnownUid(folder, remoteFolder, initialUids.Select(a => a.Id));
            }
            else
            {
                var minUid = new UniqueId(folder.HighestKnownUid + 1);
                var deltaUids = await remoteFolder
                    .SearchAsync(SearchQuery.Uids(new UniqueIdRange(minUid, UniqueId.MaxValue)), cancellationToken)
                    .ConfigureAwait(false);

                downloadedMessageIds = await DownloadMessagesByUidsAsync(client, remoteFolder, folder, deltaUids, synchronizer, cancellationToken).ConfigureAwait(false);
                UpdateHighestKnownUid(folder, remoteFolder, deltaUids.Select(a => a.Id));
            }

            await ReconcileUidBasedFlagChangesAsync(folder, remoteFolder, cancellationToken).ConfigureAwait(false);

            if (ShouldRunUidReconcile(folder))
            {
                await ReconcileDeletedMessagesAsync(folder, remoteFolder, cancellationToken).ConfigureAwait(false);
            }
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

    #region Shared Helpers

    private static SearchQuery BuildInitialSyncQuery(IImapSynchronizer synchronizer)
    {
        if (synchronizer is IBaseSynchronizer { Account: { } account })
        {
            var referenceDateUtc = account.CreatedAt ?? DateTime.UtcNow;
            var cutoffDateUtc = account.InitialSynchronizationRange.ToCutoffDateUtc(referenceDateUtc);

            if (cutoffDateUtc.HasValue)
            {
                return SearchQuery.DeliveredAfter(cutoffDateUtc.Value.ToUniversalTime().Date);
            }
        }

        return SearchQuery.All;
    }

    private async Task EnsureUidValidityStateAsync(MailItemFolder folder, IMailFolder remoteFolder)
    {
        if (folder.UidValidity != 0 && remoteFolder.UidValidity != folder.UidValidity)
        {
            _logger.Warning("UIDVALIDITY changed for folder {FolderName}. Resetting local folder state.", folder.FolderName);

            var existingMails = await _mailService.GetMailsByFolderIdAsync(folder.Id).ConfigureAwait(false);
            foreach (var mail in existingMails)
            {
                await _mailService.DeleteMailAsync(folder.MailAccountId, mail.Id).ConfigureAwait(false);
            }

            folder.HighestKnownUid = 0;
            folder.HighestModeSeq = 0;
            folder.LastUidReconcileUtc = null;
        }

        folder.UidValidity = remoteFolder.UidValidity;
    }

    private Task<List<string>> ProcessSummariesAsync(
        IImapSynchronizer synchronizer,
        MailItemFolder localFolder,
        IList<IMessageSummary> summaries,
        CancellationToken cancellationToken)
        => ProcessSummariesCoreAsync(synchronizer, localFolder, summaries, existingByUid: null, cancellationToken);

    private async Task<List<string>> ProcessSummariesCoreAsync(
        IImapSynchronizer synchronizer,
        MailItemFolder localFolder,
        IList<IMessageSummary> summaries,
        IReadOnlyDictionary<uint, MailCopy> existingByUid,
        CancellationToken cancellationToken)
    {
        var downloadedMessageIds = new List<string>();

        if (summaries == null || summaries.Count == 0)
            return downloadedMessageIds;

        var uniqueIds = summaries
            .Where(s => s.UniqueId != UniqueId.Invalid)
            .Select(s => s.UniqueId)
            .ToList();

        if (uniqueIds.Count == 0)
            return downloadedMessageIds;

        existingByUid ??= CreateExistingMailLookup(await _mailService.GetExistingMailsAsync(localFolder.Id, uniqueIds).ConfigureAwait(false));
        var pendingStateUpdates = new List<MailCopyStateUpdate>();

        foreach (var summary in summaries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (summary.UniqueId == UniqueId.Invalid)
                continue;

            if (existingByUid.TryGetValue(summary.UniqueId.Id, out var existingMail))
            {
                if (summary.Flags != null)
                {
                    var pendingStateUpdate = CreateMailStateUpdate(existingMail, summary.Flags.Value);
                    if (pendingStateUpdate != null)
                    {
                        pendingStateUpdates.Add(pendingStateUpdate);
                    }
                }

                continue;
            }

            var creationPackage = new ImapMessageCreationPackage(summary, mimeMessage: null);
            var mailPackages = await synchronizer.CreateNewMailPackagesAsync(creationPackage, localFolder, cancellationToken).ConfigureAwait(false);

            if (mailPackages == null)
                continue;

            foreach (var package in mailPackages)
            {
                if (package == null)
                    continue;

                var inserted = await _mailService.CreateMailAsync(localFolder.MailAccountId, package).ConfigureAwait(false);
                if (inserted)
                {
                    downloadedMessageIds.Add(package.Copy.Id);
                }
            }
        }

        if (pendingStateUpdates.Count > 0)
        {
            await _mailService.ApplyMailStateUpdatesAsync(pendingStateUpdates).ConfigureAwait(false);
        }

        return downloadedMessageIds;
    }

    private async Task ApplySummaryFlagUpdatesAsync(
        IReadOnlyDictionary<uint, MailCopy> existingByUid,
        IList<IMessageSummary> summaries)
    {
        if (existingByUid == null || existingByUid.Count == 0 || summaries == null || summaries.Count == 0)
            return;

        var pendingStateUpdates = new List<MailCopyStateUpdate>();

        foreach (var summary in summaries)
        {
            if (summary.UniqueId == UniqueId.Invalid || summary.Flags == null)
                continue;

            if (!existingByUid.TryGetValue(summary.UniqueId.Id, out var existingMail))
                continue;

            var pendingStateUpdate = CreateMailStateUpdate(existingMail, summary.Flags.Value);
            if (pendingStateUpdate != null)
            {
                pendingStateUpdates.Add(pendingStateUpdate);
            }
        }

        if (pendingStateUpdates.Count > 0)
        {
            await _mailService.ApplyMailStateUpdatesAsync(pendingStateUpdates).ConfigureAwait(false);
        }
    }

    private static IReadOnlyDictionary<uint, MailCopy> CreateExistingMailLookup(IEnumerable<MailCopy> existingMails)
    {
        var lookup = new Dictionary<uint, MailCopy>();

        foreach (var mail in existingMails ?? [])
        {
            if (mail == null || string.IsNullOrEmpty(mail.Id))
                continue;

            try
            {
                lookup[MailkitClientExtensions.ResolveUidStruct(mail.Id).Id] = mail;
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        return lookup;
    }

    private static MailCopyStateUpdate CreateMailStateUpdate(MailCopy mailCopy, MessageFlags flags)
    {
        var isFlagged = MailkitClientExtensions.GetIsFlagged(flags);
        var isRead = MailkitClientExtensions.GetIsRead(flags);

        bool shouldUpdateFlagged = isFlagged != mailCopy.IsFlagged;
        bool shouldUpdateRead = isRead != mailCopy.IsRead;

        return !shouldUpdateFlagged && !shouldUpdateRead
            ? null
            : new MailCopyStateUpdate(
                mailCopy.Id,
                shouldUpdateRead ? isRead : null,
                shouldUpdateFlagged ? isFlagged : null);
    }

    private async Task ApplyDeletedUidsAsync(MailItemFolder folder, IList<UniqueId> uniqueIds)
    {
        if (uniqueIds == null || uniqueIds.Count == 0)
            return;

        foreach (var uniqueId in uniqueIds.Distinct())
        {
            var localMailCopyId = MailkitClientExtensions.CreateUid(folder.Id, uniqueId.Id);
            await _mailService.DeleteMailAsync(folder.MailAccountId, localMailCopyId).ConfigureAwait(false);
        }
    }

    private async Task ApplyFlagChangesAsync(MailItemFolder folder, IDictionary<uint, MessageFlags> changedFlags)
    {
        if (changedFlags == null || changedFlags.Count == 0)
            return;

        var stateUpdates = changedFlags
            .Select(changed => new MailCopyStateUpdate(
                MailkitClientExtensions.CreateUid(folder.Id, changed.Key),
                MailkitClientExtensions.GetIsRead(changed.Value),
                MailkitClientExtensions.GetIsFlagged(changed.Value)))
            .ToList();

        await _mailService.ApplyMailStateUpdatesAsync(stateUpdates).ConfigureAwait(false);
    }

    private async Task ReconcileUidBasedFlagChangesAsync(MailItemFolder localFolder, IMailFolder remoteFolder, CancellationToken cancellationToken)
    {
        var localMails = await _mailService.GetMailsByFolderIdAsync(localFolder.Id).ConfigureAwait(false);

        if (localMails == null || localMails.Count == 0)
            return;

        var localByUid = new Dictionary<uint, MailCopy>();
        var localUnreadUids = new HashSet<uint>();
        var localFlaggedUids = new HashSet<uint>();

        foreach (var localMail in localMails)
        {
            if (localMail == null || string.IsNullOrEmpty(localMail.Id))
                continue;

            uint uid;
            try
            {
                uid = MailkitClientExtensions.ResolveUid(localMail.Id);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            localByUid[uid] = localMail;

            if (!localMail.IsRead)
                localUnreadUids.Add(uid);

            if (localMail.IsFlagged)
                localFlaggedUids.Add(uid);
        }

        if (localByUid.Count == 0)
            return;

        var remoteUnreadUids = (await remoteFolder.SearchAsync(SearchQuery.NotSeen, cancellationToken).ConfigureAwait(false))
            .Select(a => a.Id)
            .ToHashSet();
        var remoteFlaggedUids = (await remoteFolder.SearchAsync(SearchQuery.Flagged, cancellationToken).ConfigureAwait(false))
            .Select(a => a.Id)
            .ToHashSet();

        var markReadCandidates = localUnreadUids.Except(remoteUnreadUids).ToList();
        var unflagCandidates = localFlaggedUids.Except(remoteFlaggedUids).ToList();

        var existingMarkReadCandidates = await FilterExistingRemoteUidsAsync(remoteFolder, markReadCandidates, cancellationToken).ConfigureAwait(false);
        var existingUnflagCandidates = await FilterExistingRemoteUidsAsync(remoteFolder, unflagCandidates, cancellationToken).ConfigureAwait(false);
        var pendingStateUpdates = new List<MailCopyStateUpdate>();

        foreach (var uid in existingMarkReadCandidates)
        {
            if (!localByUid.TryGetValue(uid, out var localMail) || localMail.IsRead)
                continue;

            pendingStateUpdates.Add(new MailCopyStateUpdate(localMail.Id, IsRead: true));
        }

        foreach (var uid in remoteUnreadUids)
        {
            if (!localByUid.TryGetValue(uid, out var localMail) || !localMail.IsRead)
                continue;

            pendingStateUpdates.Add(new MailCopyStateUpdate(localMail.Id, IsRead: false));
        }

        foreach (var uid in existingUnflagCandidates)
        {
            if (!localByUid.TryGetValue(uid, out var localMail) || !localMail.IsFlagged)
                continue;

            pendingStateUpdates.Add(new MailCopyStateUpdate(localMail.Id, IsFlagged: false));
        }

        foreach (var uid in remoteFlaggedUids)
        {
            if (!localByUid.TryGetValue(uid, out var localMail) || localMail.IsFlagged)
                continue;

            pendingStateUpdates.Add(new MailCopyStateUpdate(localMail.Id, IsFlagged: true));
        }

        if (pendingStateUpdates.Count > 0)
        {
            await _mailService.ApplyMailStateUpdatesAsync(pendingStateUpdates).ConfigureAwait(false);
        }
    }

    private static async Task<HashSet<uint>> FilterExistingRemoteUidsAsync(IMailFolder remoteFolder, IEnumerable<uint> candidateUids, CancellationToken cancellationToken)
    {
        var existing = new HashSet<uint>();
        var uidList = candidateUids?.Distinct().ToList();

        if (uidList == null || uidList.Count == 0)
            return existing;

        foreach (var batch in uidList.Batch(200))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchUids = batch.Select(a => new UniqueId(a)).ToList();
            var existingBatch = await remoteFolder
                .SearchAsync(SearchQuery.Uids(new UniqueIdSet(batchUids, SortOrder.Ascending)), cancellationToken)
                .ConfigureAwait(false);

            foreach (var existingUid in existingBatch)
            {
                existing.Add(existingUid.Id);
            }
        }

        return existing;
    }

    private bool ShouldRunUidReconcile(MailItemFolder folder)
    {
        return ShouldRunUidReconcile(folder.LastUidReconcileUtc, DateTime.UtcNow, UidReconcileInterval);
    }

    private async Task ReconcileDeletedMessagesAsync(MailItemFolder localFolder, IMailFolder remoteFolder, CancellationToken cancellationToken)
    {
        var allLocalUids = (await _folderService.GetKnownUidsForFolderAsync(localFolder.Id).ConfigureAwait(false))
            .Select(a => new UniqueId(a))
            .ToList();

        if (allLocalUids.Count == 0)
        {
            localFolder.LastUidReconcileUtc = DateTime.UtcNow;
            return;
        }

        var remoteAllUids = await remoteFolder.SearchAsync(SearchQuery.All, cancellationToken).ConfigureAwait(false);
        var deletedUids = allLocalUids.Except(remoteAllUids).ToList();

        await ApplyDeletedUidsAsync(localFolder, deletedUids).ConfigureAwait(false);
        localFolder.LastUidReconcileUtc = DateTime.UtcNow;
    }

    private static void UpdateHighestKnownUid(MailItemFolder folder, IMailFolder remoteFolder, IEnumerable<uint> observedUids)
    {
        folder.HighestKnownUid = CalculateHighestKnownUid(folder.HighestKnownUid, remoteFolder?.UidNext, observedUids);
    }

    public static bool ShouldRunUidReconcile(DateTime? lastUidReconcileUtc, DateTime utcNow, TimeSpan reconcileInterval)
    {
        if (!lastUidReconcileUtc.HasValue)
        {
            return true;
        }

        return utcNow - lastUidReconcileUtc.Value >= reconcileInterval;
    }

    public static uint CalculateHighestKnownUid(uint currentHighestKnownUid, UniqueId? uidNext, IEnumerable<uint> observedUids)
    {
        uint observedMax = 0;

        if (observedUids != null)
        {
            foreach (var uid in observedUids)
            {
                if (uid > observedMax)
                {
                    observedMax = uid;
                }
            }
        }

        uint uidNextBased = 0;
        if (uidNext.HasValue)
        {
            uidNextBased = uidNext.Value.Id > 0 ? uidNext.Value.Id - 1 : 0;
        }

        return Math.Max(currentHighestKnownUid, Math.Max(observedMax, uidNextBased));
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
    /// UID-based delta synchronization fallback.
    /// </summary>
    UidBased
}
