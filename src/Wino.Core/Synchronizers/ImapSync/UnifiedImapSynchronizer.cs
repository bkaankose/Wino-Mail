using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MoreLinq;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Extensions;
using Wino.Core.Integration;
using Wino.Services;
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
    private const int NewMessageFetchBatchSize = 200;

    private readonly ConcurrentDictionary<Guid, ImapSyncStrategy> _mailboxFallbacks = new();
    private readonly ConcurrentDictionary<Guid, byte> _repairedFolders = new();
    private readonly ConcurrentDictionary<Guid, byte> _unexplainedModSeqFolders = new();

    // UIDs of messages that are still in the mailbox with \Deleted (RFC 3501 section 2.3.2) and therefore not
    // cached. They stay in the flag checks so that removing \Deleted again restores the message. In memory only:
    // the repair pass of the first synchronization in a process rebuilds it.
    private readonly ConcurrentDictionary<Guid, HashSet<uint>> _deletedFlagUids = new();

    private readonly ILogger _logger = Log.ForContext<UnifiedImapSynchronizer>();
    private readonly IFolderService _folderService;
    private readonly IMailService _mailService;
    private readonly IImapSynchronizerErrorHandlerFactory _errorHandlerFactory;
    private readonly IMailFilterExecutor _mailFilterExecutor;
    private readonly IKnownImapProviderCatalog _knownImapProviderCatalog;

    // Metadata-first synchronization flags: no full MIME body download.
    private readonly MessageSummaryItems _mailSynchronizationFlags =
        MessageSummaryItems.Flags |
        MessageSummaryItems.UniqueId |
        MessageSummaryItems.InternalDate |
        MessageSummaryItems.Envelope |
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
        IImapSynchronizerErrorHandlerFactory errorHandlerFactory,
        IMailFilterExecutor mailFilterExecutor = null,
        IKnownImapProviderCatalog knownImapProviderCatalog = null)
    {
        _folderService = folderService;
        _mailService = mailService;
        _errorHandlerFactory = errorHandlerFactory;
        _knownImapProviderCatalog = knownImapProviderCatalog ??
            new EmbeddedKnownImapProviderCatalog(new KnownImapProviderCatalogLoader());
        _mailFilterExecutor = mailFilterExecutor;
    }

    /// <summary>
    /// Resolves one special role per remote folder. Server-declared roles are always evaluated;
    /// provider and generic aliases are used only during the one-time account bootstrap.
    /// </summary>
    public IReadOnlyDictionary<string, SpecialFolderType> ResolveKnownFolders(
        IImapClient client,
        MailAccount account,
        IReadOnlyList<IMailFolder> remoteFolders,
        IReadOnlyList<MailItemFolder> localFolders)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(account);

        remoteFolders ??= [];
        localFolders ??= [];

        var resolved = new Dictionary<string, SpecialFolderType>(StringComparer.Ordinal);
        var resolvedRoles = new HashSet<SpecialFolderType>();
        var specialReferences = GetSpecialFolderReferences(client);

        var declaredFolders = remoteFolders
            .Select(folder => new
            {
                Folder = folder,
                Role = GetServerDeclaredRole(client, folder, specialReferences)
            })
            .Where(item => item.Role != SpecialFolderType.Other)
            .GroupBy(item => item.Role);

        foreach (var roleGroup in declaredFolders)
        {
            var candidates = roleGroup.Select(item => item.Folder).ToList();
            resolvedRoles.Add(roleGroup.Key);

            var selected = candidates.Count == 1
                ? candidates[0]
                : candidates.SingleOrDefault(candidate => IsPreferredServerReference(
                    client,
                    candidate,
                    roleGroup.Key,
                    specialReferences));
            selected ??= candidates.SingleOrDefault(candidate => localFolders.Any(local =>
                local.RemoteFolderId == candidate.FullName && local.SpecialFolderType == roleGroup.Key));

            if (selected == null)
            {
                _logger.Warning(
                    "Multiple IMAP folders declare the server role {Role}: {Folders}. The role remains unassigned.",
                    roleGroup.Key,
                    string.Join(", ", candidates.Select(candidate => candidate.FullName)));
                continue;
            }

            resolved[selected.FullName] = roleGroup.Key;
        }

        if (account.ImapKnownFolderBootstrapState != ImapKnownFolderBootstrapState.Pending)
            return resolved;

        var provider = _knownImapProviderCatalog.Match(
            account.Address,
            account.ServerInformation?.IncomingServer,
            account.SpecialImapProvider);

        ApplyAliases(provider?.FolderAliases, remoteFolders, localFolders, resolved, resolvedRoles, "provider");
        ApplyAliases(_knownImapProviderCatalog.GenericFolderAliases, remoteFolders, localFolders, resolved, resolvedRoles, "generic");

        return resolved;
    }

    private void ApplyAliases(
        IReadOnlyList<KnownImapFolderAlias> aliases,
        IReadOnlyList<IMailFolder> remoteFolders,
        IReadOnlyList<MailItemFolder> localFolders,
        IDictionary<string, SpecialFolderType> resolved,
        ISet<SpecialFolderType> resolvedRoles,
        string source)
    {
        if (aliases == null)
            return;

        foreach (var roleGroup in aliases.GroupBy(alias => alias.Role))
        {
            if (resolvedRoles.Contains(roleGroup.Key))
                continue;

            var candidates = remoteFolders
                .Where(folder => !resolved.ContainsKey(folder.FullName))
                .Where(folder => roleGroup.Any(alias => MatchesAlias(folder, alias)))
                .GroupBy(folder => folder.FullName, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            if (candidates.Count == 0)
                continue;

            IMailFolder selected = candidates.Count == 1
                ? candidates[0]
                : candidates.FirstOrDefault(candidate => localFolders.Any(local =>
                    local.RemoteFolderId == candidate.FullName && local.SpecialFolderType == roleGroup.Key));

            if (selected == null)
            {
                _logger.Warning(
                    "Ambiguous {Source} IMAP folder aliases for role {Role}: {Folders}. The role remains unassigned.",
                    source,
                    roleGroup.Key,
                    string.Join(", ", candidates.Select(candidate => candidate.FullName)));
                continue;
            }

            resolved[selected.FullName] = roleGroup.Key;
            resolvedRoles.Add(roleGroup.Key);
        }
    }

    private static bool MatchesAlias(IMailFolder folder, KnownImapFolderAlias alias)
    {
        if (alias.MatchFullPath)
            return string.Equals(folder.FullName, alias.Value, StringComparison.OrdinalIgnoreCase);

        var isTopLevel = folder.ParentFolder == null || folder.ParentFolder.IsNamespace;
        return isTopLevel && string.Equals(folder.Name, alias.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static SpecialFolderType GetServerDeclaredRole(
        IImapClient client,
        IMailFolder folder,
        IReadOnlyDictionary<SpecialFolder, IMailFolder> specialReferences)
    {
        if (ReferenceEquals(folder, client.Inbox) ||
            folder.Attributes.HasFlag(FolderAttributes.Inbox) ||
            string.Equals(folder.FullName, "INBOX", StringComparison.OrdinalIgnoreCase))
        {
            return SpecialFolderType.Inbox;
        }

        if (folder.Attributes.HasFlag(FolderAttributes.Drafts) || IsSpecialReference(folder, SpecialFolder.Drafts, specialReferences))
            return SpecialFolderType.Draft;
        if (folder.Attributes.HasFlag(FolderAttributes.Sent) || IsSpecialReference(folder, SpecialFolder.Sent, specialReferences))
            return SpecialFolderType.Sent;
        if (folder.Attributes.HasFlag(FolderAttributes.Trash) || IsSpecialReference(folder, SpecialFolder.Trash, specialReferences))
            return SpecialFolderType.Deleted;
        if (folder.Attributes.HasFlag(FolderAttributes.Junk) || IsSpecialReference(folder, SpecialFolder.Junk, specialReferences))
            return SpecialFolderType.Junk;
        if (folder.Attributes.HasFlag(FolderAttributes.Archive) || IsSpecialReference(folder, SpecialFolder.Archive, specialReferences))
            return SpecialFolderType.Archive;
        if (folder.Attributes.HasFlag(FolderAttributes.Important) || IsSpecialReference(folder, SpecialFolder.Important, specialReferences))
            return SpecialFolderType.Important;
        if (folder.Attributes.HasFlag(FolderAttributes.Flagged) || IsSpecialReference(folder, SpecialFolder.Flagged, specialReferences))
            return SpecialFolderType.Starred;

        // FolderAttributes.All is intentionally ignored: Wino does not model aggregate mailboxes safely.
        return SpecialFolderType.Other;
    }

    private static bool IsSpecialReference(
        IMailFolder folder,
        SpecialFolder role,
        IReadOnlyDictionary<SpecialFolder, IMailFolder> specialReferences)
        => specialReferences.TryGetValue(role, out var reference) &&
           (ReferenceEquals(folder, reference) ||
            string.Equals(folder.FullName, reference.FullName, StringComparison.Ordinal));

    private static bool IsPreferredServerReference(
        IImapClient client,
        IMailFolder folder,
        SpecialFolderType role,
        IReadOnlyDictionary<SpecialFolder, IMailFolder> specialReferences)
        => role switch
        {
            SpecialFolderType.Inbox => ReferenceEquals(folder, client.Inbox),
            SpecialFolderType.Draft => IsSpecialReference(folder, SpecialFolder.Drafts, specialReferences),
            SpecialFolderType.Sent => IsSpecialReference(folder, SpecialFolder.Sent, specialReferences),
            SpecialFolderType.Deleted => IsSpecialReference(folder, SpecialFolder.Trash, specialReferences),
            SpecialFolderType.Junk => IsSpecialReference(folder, SpecialFolder.Junk, specialReferences),
            SpecialFolderType.Archive => IsSpecialReference(folder, SpecialFolder.Archive, specialReferences),
            SpecialFolderType.Important => IsSpecialReference(folder, SpecialFolder.Important, specialReferences),
            SpecialFolderType.Starred => IsSpecialReference(folder, SpecialFolder.Flagged, specialReferences),
            _ => false
        };

    private static IReadOnlyDictionary<SpecialFolder, IMailFolder> GetSpecialFolderReferences(IImapClient client)
    {
        var references = new Dictionary<SpecialFolder, IMailFolder>();
        // MailKit throws for every special-folder lookup when neither extension is advertised.
        // LIST attributes and bootstrap aliases are still resolved by the caller.
        if ((client.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) == 0)
            return references;

        foreach (var role in new[]
        {
            SpecialFolder.Drafts,
            SpecialFolder.Sent,
            SpecialFolder.Trash,
            SpecialFolder.Junk,
            SpecialFolder.Archive,
            SpecialFolder.Important,
            SpecialFolder.Flagged
        })
        {
            try
            {
                var folder = client.GetFolder(role);
                if (folder != null)
                    references[role] = folder;
            }
            catch (NotSupportedException)
            {
                // A server may advertise the extension without exposing every optional role.
            }
        }

        return references;
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
        CancellationToken cancellationToken = default,
        bool suppressMatchingLocalFilters = false)
    {
        var strategy = DetermineSyncStrategy(client, serverHost);
        _logger.Verbose("Using {Strategy} sync strategy for folder {FolderName}", strategy, folder.FolderName);

        var originalHighestModeSeq = folder.HighestModeSeq;
        var originalUidValidity = folder.UidValidity;
        var originalHighestKnownUid = folder.HighestKnownUid;
        var originalLastUidReconcileUtc = folder.LastUidReconcileUtc;

        try
        {
            var changes = new MailboxChanges();
            await SynchronizeMailboxAsync(
                client, folder, synchronizer, strategy, changes, cancellationToken, suppressMatchingLocalFilters).ConfigureAwait(false);

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

            _repairedFolders.TryAdd(folder.Id, 0);
            var result = FolderSyncResult.Successful(folder.Id, folder.FolderName, changes.Downloaded, changes.Deleted, changes.Updated);
            result.DownloadedMailIds = changes.NewMailIds;
            return result;
        }
        catch (OperationCanceledException)
        {
            RestoreCheckpoint();
            throw;
        }
        catch (Exception ex)
        {
            RestoreCheckpoint();
            var errorContext = new SynchronizerErrorContext
            {
                Account = (synchronizer as IWinoSynchronizerBase)?.Account,
                ErrorMessage = ex.Message,
                Exception = ex,
                FolderId = folder.Id,
                FolderName = folder.FolderName,
                OperationType = "ImapFolderSync"
            };

            _ = await _errorHandlerFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);

            if (errorContext.CanContinueSync || errorContext.Severity == SynchronizerErrorSeverity.Transient)
            {
                _logger.Warning(ex, "Folder {FolderName} sync failed with recoverable error", folder.FolderName);
                return FolderSyncResult.Failed(folder.Id, folder.FolderName, errorContext);
            }

            _logger.Error(ex, "Folder {FolderName} sync failed with fatal error", folder.FolderName);
            throw;
        }

        void RestoreCheckpoint()
        {
            folder.HighestModeSeq = originalHighestModeSeq;
            folder.UidValidity = originalUidValidity;
            folder.HighestKnownUid = originalHighestKnownUid;
            folder.LastUidReconcileUtc = originalLastUidReconcileUtc;
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
        CancellationToken cancellationToken = default,
        bool suppressMatchingLocalFilters = false)
    {
        if (uids == null || uids.Count == 0)
            return [];

        if (!remoteFolder.IsOpen)
            await remoteFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

        // Online search can reach a folder that was never synchronized. Caching its messages establishes the
        // folder's UIDVALIDITY (RFC 3501 section 2.3.1.1); the first synchronization then continues from it.
        if (localFolder.UidValidity == 0 && remoteFolder.UidValidity != 0)
        {
            localFolder.UidValidity = await _folderService
                .InitializeFolderUidValidityAsync(localFolder.Id, remoteFolder.UidValidity).ConfigureAwait(false);
        }

        if (localFolder.UidValidity == 0 || remoteFolder.UidValidity != localFolder.UidValidity)
            throw new InvalidOperationException("IMAP mailbox identity changed. Synchronize the folder before downloading messages.");

        var downloadedMessageIds = new List<string>();

        foreach (var batch in uids.Distinct().OrderBy(a => a.Id).Batch(NewMessageFetchBatchSize))
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
                    .FetchAsync(new UniqueIdSet(newBatch.ToList(), SortOrder.Ascending),
                        new FetchRequest(_mailSynchronizationFlags, new[]
                        {
                            "References", "Subject", "From", "Sender", Domain.Constants.WinoLocalDraftHeader, Domain.Constants.LegacyWinoLocalDraftHeader, Domain.Constants.DispositionNotificationToHeader
                        }), cancellationToken)
                    .ConfigureAwait(false);

                // MailKit can include unsolicited FETCH responses. Only requested metadata belongs
                // to this batch; a flags-only response must never create a blank cached message.
                var requested = newBatch.ToHashSet();
                newSummaryBatch = newSummaryBatch.Where(summary => requested.Contains(summary.UniqueId)).ToList();
                if (newSummaryBatch.Any(summary => summary.Envelope == null || !summary.Flags.HasValue || !summary.InternalDate.HasValue))
                    throw new IOException("IMAP metadata reconciliation returned an incomplete message.");

                var missing = requested.Except(newSummaryBatch.Select(summary => summary.UniqueId)).ToList();
                if (missing.Count > 0)
                {
                    var stillPresent = await remoteFolder.SearchAsync(
                        SearchQuery.Uids(new UniqueIdSet(missing, SortOrder.Ascending)), cancellationToken).ConfigureAwait(false);
                    if (stillPresent.Count > 0)
                        throw new IOException("IMAP FETCH omitted messages that still exist. The synchronization checkpoint was not advanced.");
                }

                downloadedMessageIds.AddRange(await ProcessSummariesCoreAsync(
                    synchronizer,
                    localFolder,
                    newSummaryBatch,
                    existingByUid,
                    cancellationToken,
                    suppressMatchingLocalFilters).ConfigureAwait(false));
            }
        }

        return downloadedMessageIds;
    }

    #region Strategy Implementations

    private sealed class MailboxChanges
    {
        public List<string> NewMailIds { get; } = [];
        public int Downloaded { get; set; }
        public int Updated { get; set; }
        public int Deleted { get; set; }
    }

    private async Task SynchronizeMailboxAsync(
        IImapClient client,
        MailItemFolder folder,
        IImapSynchronizer synchronizer,
        ImapSyncStrategy strategy,
        MailboxChanges changes,
        CancellationToken cancellationToken,
        bool suppressMatchingLocalFilters)
    {
        var localMails = await _mailService.GetImapSynchronizationMailsAsync(folder.Id).ConfigureAwait(false) ?? [];
        var known = CreateExistingMailLookup(localMails.Where(mail => !mail.IsLocalDraft));
        var vanished = new HashSet<UniqueId>();
        var changedFlags = new Dictionary<uint, MessageFlags>();
        var remote = await client.GetFolderAsync(folder.RemoteFolderId, cancellationToken).ConfigureAwait(false);
        var savedValidity = folder.UidValidity;
        var savedModSeq = folder.HighestModeSeq;
        var usedQResync = false;
        var openedWithQResync = false;

        if (_mailboxFallbacks.TryGetValue(folder.Id, out var fallback) && fallback > strategy)
            strategy = fallback;

        var deletedFlagUids = _deletedFlagUids.GetOrAdd(folder.Id, _ => []);
        HashSet<uint> deletedFlagUidsBefore;
        lock (deletedFlagUids)
            deletedFlagUidsBefore = [.. deletedFlagUids];
        List<uint> FlagCheckedUids()
        {
            lock (deletedFlagUids)
                return known.Keys.Concat(deletedFlagUids).Distinct().ToList();
        }

        void OnVanished(object sender, MessagesVanishedEventArgs args) => vanished.UnionWith(args.UniqueIds);
        void OnFlags(object sender, MessageFlagsChangedEventArgs args)
        {
            if (args.UniqueId is UniqueId uid)
                changedFlags[uid.Id] = args.Flags;
        }

        remote.MessagesVanished += OnVanished;
        remote.MessageFlagsChanged += OnFlags;

        try
        {
            if (strategy == ImapSyncStrategy.QResync && savedValidity != 0 && savedModSeq > 0)
            {
                try
                {
                    // MailKit compresses consecutive UIDs. SELECT itself validates UIDVALIDITY.
                    await remote.OpenAsync(FolderAccess.ReadOnly, savedValidity, (ulong)savedModSeq,
                        new UniqueIdSet(FlagCheckedUids().Select(uid => new UniqueId(uid)), SortOrder.Ascending), cancellationToken).ConfigureAwait(false);
                    usedQResync = true;
                    openedWithQResync = true;
                }
                catch (Exception ex) when (IsExtensionRejection(ex) && client.IsConnected)
                {
                    strategy = ImapSyncStrategy.Condstore;
                    _mailboxFallbacks[folder.Id] = strategy;
                    _logger.Warning(ex, "QRESYNC rejected for folder {FolderId}; using a simpler synchronization strategy.", folder.Id);
                }
            }

            if (!usedQResync)
                await remote.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

            changes.Deleted += await EnsureUidValidityStateAsync(folder, remote).ConfigureAwait(false);
            var unknownUndeletedUids = new HashSet<uint>();
            if (savedValidity != folder.UidValidity)
            {
                known = CreateExistingMailLookup((await _mailService.GetImapSynchronizationMailsAsync(folder.Id).ConfigureAwait(false) ?? [])
                    .Where(mail => !mail.IsLocalDraft));
                vanished.Clear();
                changedFlags.Clear();
                usedQResync = false;
                lock (deletedFlagUids)
                    deletedFlagUids.Clear();
                deletedFlagUidsBefore.Clear();
            }

            // Capture the server frontier before SEARCH/FETCH can observe later arrivals.
            var uidBoundary = remote.UidNext?.Id > 0 ? remote.UidNext.Value.Id - 1 : (uint?)null;
            var modSeqBoundary = remote.HighestModSeq;
            var supportsModSeq = remote.Supports(FolderFeature.ModSequences) && modSeqBoundary > 0;
            var canUseDelta = supportsModSeq && folder.HighestModeSeq > 0 && modSeqBoundary >= (ulong)folder.HighestModeSeq;
            if (!supportsModSeq || (folder.HighestModeSeq > 0 && modSeqBoundary < (ulong)folder.HighestModeSeq))
            {
                strategy = ImapSyncStrategy.UidBased;
                usedQResync = false;
            }

            var initial = folder.HighestKnownUid == 0;
            var repair = initial || !_repairedFolders.ContainsKey(folder.Id) || ShouldRunUidReconcile(folder);
            IList<UniqueId> discovered = new List<UniqueId>();
            if (repair)
            {
                // Also repairs rows missing below an old checkpoint, including legacy deduplication losses.
                discovered = await remote.SearchAsync(BuildInitialSyncQuery(synchronizer).And(SearchQuery.NotDeleted), cancellationToken).ConfigureAwait(false);
                var flaggedDeleted = await remote.SearchAsync(SearchQuery.Deleted, cancellationToken).ConfigureAwait(false);
                lock (deletedFlagUids)
                {
                    deletedFlagUids.Clear();
                    deletedFlagUids.UnionWith(flaggedDeleted.Select(uid => uid.Id));
                }
            }
            else if (folder.HighestKnownUid < uint.MaxValue && (!uidBoundary.HasValue || uidBoundary.Value > folder.HighestKnownUid))
            {
                var range = new UniqueIdRange(new UniqueId(folder.HighestKnownUid + 1),
                    uidBoundary.HasValue ? new UniqueId(uidBoundary.Value) : UniqueId.MaxValue);
                discovered = await remote.SearchAsync(SearchQuery.Uids(range).And(SearchQuery.NotDeleted), cancellationToken).ConfigureAwait(false);
            }

            var missingUids = discovered.Where(uid => !known.ContainsKey(uid.Id)).ToList();
            var newMailIds = await DownloadMessagesByUidsAsync(client, remote, folder, missingUids,
                synchronizer, cancellationToken, suppressMatchingLocalFilters).ConfigureAwait(false);
            changes.NewMailIds.AddRange(newMailIds);
            changes.Downloaded += newMailIds.Count;

            var membershipReconciled = known.Count == 0;
            if (known.Count > 0)
            {
                // Some servers (Purelymail, Apache James) advance HIGHESTMODSEQ but omit the FETCH responses that
                // RFC 7162 requires on a QRESYNC open; Purelymail answers CHANGEDSINCE with nothing as well. Trusting that
                // empty report would move the checkpoint past changes that were never applied. A compliant server
                // explains every MODSEQ advance with a change, a vanished UID or a new message, so only an
                // unexplained advance falls back to a full FLAGS fetch.
                var unexplainedModSeqAdvance = usedQResync && canUseDelta
                                               && modSeqBoundary > (ulong)folder.HighestModeSeq
                                               && changedFlags.Count == 0 && vanished.Count == 0
                                               && missingUids.Count == 0;

                if (unexplainedModSeqAdvance && _unexplainedModSeqFolders.TryAdd(folder.Id, 0))
                    _logger.Warning("QRESYNC open for folder {FolderId} reported no changes although HIGHESTMODSEQ advanced; reconciling all flags.", folder.Id);

                if (usedQResync && canUseDelta && !unexplainedModSeqAdvance)
                {
                    var (updated, deleted) = await ApplyFlagChangesAsync(folder, changedFlags, known, unknownUndeletedUids).ConfigureAwait(false);
                    changes.Updated += updated;
                    changes.Deleted += deleted;
                    changes.Deleted += await ApplyDeletedUidsAsync(folder, vanished.ToList()).ConfigureAwait(false);
                    lock (deletedFlagUids)
                    {
                        deletedFlagUids.UnionWith(changedFlags.Where(change => change.Value.HasFlag(MessageFlags.Deleted)).Select(change => change.Key));
                        deletedFlagUids.ExceptWith(vanished.Select(uid => uid.Id));
                    }
                }
                else
                {
                    var flagsRequest = new FetchRequest(_existingMailSynchronizationFlags);
                    if (strategy != ImapSyncStrategy.UidBased && canUseDelta && !unexplainedModSeqAdvance)
                        flagsRequest.ChangedSince = (ulong)folder.HighestModeSeq;

                    // A compact range avoids thousands of UID arguments and repeated SEARCH round trips. UIDs kept
                    // only because of \Deleted are fetched separately so they never widen a full FLAGS fetch.
                    var range = new UniqueIdRange(new UniqueId(known.Keys.Min()), new UniqueId(known.Keys.Max()));
                    var outsideRange = FlagCheckedUids().Where(uid => uid < range.Start.Id || uid > range.End.Id)
                        .Select(uid => new UniqueId(uid)).ToList();
                    async Task<IList<IMessageSummary>> FetchFlagsAsync()
                    {
                        var fetched = await remote.FetchAsync(range, flagsRequest, cancellationToken).ConfigureAwait(false);
                        if (outsideRange.Count == 0)
                            return fetched;
                        var extra = await remote.FetchAsync(new UniqueIdSet(outsideRange, SortOrder.Ascending), flagsRequest, cancellationToken).ConfigureAwait(false);
                        return fetched.Concat(extra).ToList();
                    }

                    IList<IMessageSummary> summaries;
                    try
                    {
                        summaries = await FetchFlagsAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (flagsRequest.ChangedSince.HasValue && IsExtensionRejection(ex) && client.IsConnected)
                    {
                        strategy = ImapSyncStrategy.UidBased;
                        _mailboxFallbacks[folder.Id] = strategy;
                        flagsRequest.ChangedSince = null;
                        summaries = await FetchFlagsAsync().ConfigureAwait(false);
                    }

                    if (summaries.Any(summary => summary.UniqueId.IsValid && !summary.Flags.HasValue))
                        throw new IOException("IMAP flag reconciliation returned incomplete flags.");

                    changes.Updated += await ApplySummaryFlagUpdatesAsync(known, summaries).ConfigureAwait(false);
                    changes.Deleted += await ApplyDeletedUidsAsync(folder, summaries.Where(summary => summary.Flags?.HasFlag(MessageFlags.Deleted) == true)
                        .Select(summary => summary.UniqueId).ToList()).ConfigureAwait(false);
                    unknownUndeletedUids.UnionWith(summaries
                        .Where(summary => summary.UniqueId.IsValid && !known.ContainsKey(summary.UniqueId.Id)
                                          && summary.Flags?.HasFlag(MessageFlags.Deleted) == false)
                        .Select(summary => summary.UniqueId.Id));
                    lock (deletedFlagUids)
                    {
                        deletedFlagUids.UnionWith(summaries
                            .Where(summary => summary.UniqueId.IsValid && summary.Flags?.HasFlag(MessageFlags.Deleted) == true)
                            .Select(summary => summary.UniqueId.Id));
                        if (!flagsRequest.ChangedSince.HasValue)
                            deletedFlagUids.IntersectWith(summaries.Select(summary => summary.UniqueId.Id));
                    }

                    membershipReconciled = true;
                    if (!flagsRequest.ChangedSince.HasValue)
                    {
                        // Only a successful full FLAGS response establishes absence. A delta never does.
                        var returned = summaries.Select(summary => summary.UniqueId.Id).ToHashSet();
                        changes.Deleted += await ApplyDeletedUidsAsync(folder, known.Keys.Where(uid => !returned.Contains(uid))
                            .Select(uid => new UniqueId(uid)).ToList()).ConfigureAwait(false);
                    }
                    else
                    {
                        // CONDSTORE does not report vanished UIDs. Reconcile membership on each sync.
                        changes.Deleted += await ReconcileDeletedMessagesAsync(folder, remote, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            lock (deletedFlagUids)
                deletedFlagUidsBefore.UnionWith(deletedFlagUids);
            var restored = await RestoreUndeletedMessagesAsync(client, remote, folder, synchronizer, unknownUndeletedUids,
                deletedFlagUidsBefore, discovered, changes, cancellationToken).ConfigureAwait(false);
            lock (deletedFlagUids)
                deletedFlagUids.ExceptWith(unknownUndeletedUids.Concat(restored));

            if (repair)
            {
                // Include cached messages outside the configured initial range in deletion checks.
                if (!membershipReconciled)
                    changes.Deleted += await ReconcileDeletedMessagesAsync(folder, remote, cancellationToken).ConfigureAwait(false);
                folder.LastUidReconcileUtc = DateTime.UtcNow;
            }

            var observedMax = discovered.Count > 0 ? discovered.Max(uid => uid.Id) : 0;
            folder.HighestKnownUid = Math.Max(folder.HighestKnownUid, uidBoundary ?? observedMax);
            folder.HighestModeSeq = strategy != ImapSyncStrategy.UidBased && supportsModSeq && modSeqBoundary <= long.MaxValue
                ? (long)modSeqBoundary : 0;
        }
        catch (ImapCommandException ex) when (openedWithQResync && IsSelectedMailboxLost(ex) && client.IsConnected)
        {
            // iCloud can answer OK to a QRESYNC open that reports changes and then treat the session as having no
            // mailbox selected. The restored checkpoint would send the same open on every sync, so the folder never
            // recovers unless it stops using QRESYNC.
            _mailboxFallbacks[folder.Id] = ImapSyncStrategy.Condstore;
            _logger.Warning(ex, "Folder {FolderId} lost its selected mailbox after a QRESYNC open; synchronizing it without QRESYNC.", folder.Id);
            remote.MessagesVanished -= OnVanished;
            remote.MessageFlagsChanged -= OnFlags;

            await SynchronizeMailboxAsync(client, folder, synchronizer, ImapSyncStrategy.Condstore,
                changes, cancellationToken, suppressMatchingLocalFilters).ConfigureAwait(false);
        }
        finally
        {
            remote.MessagesVanished -= OnVanished;
            remote.MessageFlagsChanged -= OnFlags;
            if (remote.IsOpen && !cancellationToken.IsCancellationRequested)
                await client.CloseSelectedMailboxAsync(remote, _logger, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsExtensionRejection(Exception exception)
        => exception is NotSupportedException || exception is ImapCommandException { Response: ImapCommandResponse.Bad };

    private static bool IsSelectedMailboxLost(ImapCommandException exception)
        => exception.Response == ImapCommandResponse.Bad
            && (exception.Message.Contains("select a mailbox", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("no mailbox selected", StringComparison.OrdinalIgnoreCase));

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

    private async Task<int> EnsureUidValidityStateAsync(MailItemFolder folder, IMailFolder remoteFolder)
    {
        if (remoteFolder.UidValidity == 0)
            throw new IOException("IMAP server did not provide a valid UIDVALIDITY.");

        var deleted = 0;
        if (folder.UidValidity != 0 && remoteFolder.UidValidity != folder.UidValidity)
        {
            _logger.Warning("UIDVALIDITY changed for folder {FolderName}. Resetting local folder state.", folder.FolderName);

            var existingMails = await _mailService.GetMailsByFolderIdAsync(folder.Id).ConfigureAwait(false);
            var removedIds = existingMails.Where(mail => !mail.IsLocalDraft).Select(mail => mail.Id).ToList();
            await _mailService.DeleteMailsAsync(folder.MailAccountId, removedIds).ConfigureAwait(false);
            deleted = removedIds.Count;

            folder.HighestKnownUid = 0;
            folder.HighestModeSeq = 0;
            folder.LastUidReconcileUtc = null;
        }

        folder.UidValidity = remoteFolder.UidValidity;
        return deleted;
    }

    private Task<List<string>> ProcessSummariesAsync(
        IImapSynchronizer synchronizer,
        MailItemFolder localFolder,
        IList<IMessageSummary> summaries,
        CancellationToken cancellationToken)
        => ProcessSummariesCoreAsync(
            synchronizer,
            localFolder,
            summaries,
            existingByUid: null,
            cancellationToken,
            suppressMatchingLocalFilters: false);

    private async Task<List<string>> ProcessSummariesCoreAsync(
        IImapSynchronizer synchronizer,
        MailItemFolder localFolder,
        IList<IMessageSummary> summaries,
        IReadOnlyDictionary<uint, MailCopy> existingByUid,
        CancellationToken cancellationToken,
        bool suppressMatchingLocalFilters)
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

            if (summary.UniqueId == UniqueId.Invalid || summary.Flags?.HasFlag(MessageFlags.Deleted) == true)
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

                var shouldSuppressUiChange = suppressMatchingLocalFilters
                    && _mailFilterExecutor != null
                    && await _mailFilterExecutor
                        .ShouldSuppressNewMessageAsync(
                            localFolder.MailAccountId,
                            package.AssignedRemoteFolderId,
                            package.Copy,
                            cancellationToken)
                        .ConfigureAwait(false);
                var packageToCreate = shouldSuppressUiChange
                    ? package with { SuppressUiChange = true }
                    : package;

                var inserted = await _mailService.CreateMailAsync(localFolder.MailAccountId, packageToCreate).ConfigureAwait(false);
                if (inserted)
                {
                    downloadedMessageIds.Add(package.Copy.Id);
                }
                else if (!await _mailService.IsMailExistsAsync(package.Copy.Id, localFolder.Id).ConfigureAwait(false))
                {
                    throw new IOException("IMAP message metadata was not persisted. The synchronization checkpoint was not advanced.");
                }
            }
        }

        if (pendingStateUpdates.Count > 0)
        {
            await _mailService.ApplyMailStateUpdatesAsync(pendingStateUpdates).ConfigureAwait(false);
        }

        return downloadedMessageIds;
    }

    private async Task<int> ApplySummaryFlagUpdatesAsync(
        IReadOnlyDictionary<uint, MailCopy> existingByUid,
        IList<IMessageSummary> summaries)
    {
        if (existingByUid == null || existingByUid.Count == 0 || summaries == null || summaries.Count == 0)
            return 0;

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

        return pendingStateUpdates.Count;
    }

    private static IReadOnlyDictionary<uint, MailCopy> CreateExistingMailLookup(IEnumerable<MailCopy> existingMails)
    {
        var lookup = new Dictionary<uint, MailCopy>();

        foreach (var mail in existingMails ?? [])
        {
            if (mail == null)
                continue;

            try
            {
                lookup[MailkitClientExtensions.ResolveUidStruct(mail).Id] = mail;
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

    private async Task<int> ApplyDeletedUidsAsync(MailItemFolder folder, IList<UniqueId> uniqueIds)
    {
        if (uniqueIds == null || uniqueIds.Count == 0)
            return 0;

        var existingMails = await _mailService.GetExistingMailsAsync(folder.Id, uniqueIds.Distinct()).ConfigureAwait(false);
        var removedIds = existingMails.Where(mail => !mail.IsLocalDraft).Select(mail => mail.Id).ToList();
        await _mailService.DeleteMailsAsync(folder.MailAccountId, removedIds).ConfigureAwait(false);
        return removedIds.Count;
    }

    private async Task<(int Updated, int Deleted)> ApplyFlagChangesAsync(MailItemFolder folder, IDictionary<uint, MessageFlags> changedFlags,
        IReadOnlyDictionary<uint, MailCopy> known, ISet<uint> unknownUndeletedUids)
    {
        var updates = new List<MailCopyStateUpdate>();
        var deleted = new List<UniqueId>();
        foreach (var (uid, flags) in changedFlags)
        {
            if (!known.TryGetValue(uid, out var mail))
            {
                if (!flags.HasFlag(MessageFlags.Deleted))
                    unknownUndeletedUids.Add(uid);
                continue;
            }

            if (flags.HasFlag(MessageFlags.Deleted))
                deleted.Add(new UniqueId(uid));
            else if (CreateMailStateUpdate(mail, flags) is { } update)
                updates.Add(update);
        }

        if (updates.Count > 0)
            await _mailService.ApplyMailStateUpdatesAsync(updates).ConfigureAwait(false);
        return (updates.Count, await ApplyDeletedUidsAsync(folder, deleted).ConfigureAwait(false));
    }

    /// <summary>
    /// Wino removes a message from the cache when it carries \Deleted (RFC 3501 section 2.3.2). Another client
    /// can remove the flag again before an EXPUNGE, and the change then arrives as a flag change for a UID that
    /// is no longer cached. UIDs Wino saw with \Deleted are downloaded again when they are still present, no
    /// longer \Deleted, and inside the account's initial synchronization range. They are not new mail.
    /// </summary>
    private async Task<List<uint>> RestoreUndeletedMessagesAsync(
        IImapClient client,
        IMailFolder remote,
        MailItemFolder folder,
        IImapSynchronizer synchronizer,
        IEnumerable<uint> unknownUndeletedUids,
        ISet<uint> deletedFlagUids,
        IList<UniqueId> discovered,
        MailboxChanges changes,
        CancellationToken cancellationToken)
    {
        var alreadyDiscovered = discovered.Select(uid => uid.Id).ToHashSet();
        var candidates = unknownUndeletedUids
            .Where(uid => deletedFlagUids.Contains(uid) && !alreadyDiscovered.Contains(uid))
            .Select(uid => new UniqueId(uid))
            .ToList();
        if (candidates.Count == 0)
            return [];

        var present = await remote.SearchAsync(SearchQuery.Uids(new UniqueIdSet(candidates, SortOrder.Ascending))
            .And(BuildInitialSyncQuery(synchronizer)).And(SearchQuery.NotDeleted), cancellationToken).ConfigureAwait(false);
        if (present.Count == 0)
            return [];

        var restored = await DownloadMessagesByUidsAsync(client, remote, folder, present, synchronizer, cancellationToken).ConfigureAwait(false);
        changes.Downloaded += restored.Count;
        _logger.Information("Restored {Count} message(s) in folder {FolderName} after their \\Deleted flag was removed.", restored.Count, folder.FolderName);
        return present.Select(uid => uid.Id).ToList();
    }

    private bool ShouldRunUidReconcile(MailItemFolder folder)
    {
        return ShouldRunUidReconcile(folder.LastUidReconcileUtc, DateTime.UtcNow, UidReconcileInterval);
    }

    private async Task<int> ReconcileDeletedMessagesAsync(MailItemFolder localFolder, IMailFolder remoteFolder, CancellationToken cancellationToken)
    {
        var allLocalUids = (await _folderService.GetKnownUidsForFolderAsync(localFolder.Id).ConfigureAwait(false))
            .Select(a => new UniqueId(a))
            .ToList();

        if (allLocalUids.Count == 0)
            return 0;

        var remoteAllUids = await remoteFolder.SearchAsync(SearchQuery.NotDeleted, cancellationToken).ConfigureAwait(false);
        var deletedUids = allLocalUids.Except(remoteAllUids).ToList();

        return await ApplyDeletedUidsAsync(localFolder, deletedUids).ConfigureAwait(false);
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
