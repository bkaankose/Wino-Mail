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
            var downloadedIds = await SynchronizeMailboxAsync(
                client, folder, synchronizer, strategy, cancellationToken, suppressMatchingLocalFilters).ConfigureAwait(false);

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
            return FolderSyncResult.Successful(folder.Id, folder.FolderName, downloadedIds.Count);
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
                            "References", Domain.Constants.WinoLocalDraftHeader, Domain.Constants.DispositionNotificationToHeader
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

    private async Task<List<string>> SynchronizeMailboxAsync(
        IImapClient client,
        MailItemFolder folder,
        IImapSynchronizer synchronizer,
        ImapSyncStrategy strategy,
        CancellationToken cancellationToken,
        bool suppressMatchingLocalFilters)
    {
        var downloaded = new List<string>();
        var localMails = await _mailService.GetImapSynchronizationMailsAsync(folder.Id).ConfigureAwait(false) ?? [];
        var known = CreateExistingMailLookup(localMails.Where(mail => !mail.IsLocalDraft));
        var vanished = new HashSet<UniqueId>();
        var changedFlags = new Dictionary<uint, MessageFlags>();
        var remote = await client.GetFolderAsync(folder.RemoteFolderId, cancellationToken).ConfigureAwait(false);
        var savedValidity = folder.UidValidity;
        var savedModSeq = folder.HighestModeSeq;
        var usedQResync = false;

        if (_mailboxFallbacks.TryGetValue(folder.Id, out var fallback) && fallback > strategy)
            strategy = fallback;

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
                        new UniqueIdSet(known.Keys.Select(uid => new UniqueId(uid)), SortOrder.Ascending), cancellationToken).ConfigureAwait(false);
                    usedQResync = true;
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

            await EnsureUidValidityStateAsync(folder, remote).ConfigureAwait(false);
            if (savedValidity != folder.UidValidity)
            {
                known = CreateExistingMailLookup((await _mailService.GetImapSynchronizationMailsAsync(folder.Id).ConfigureAwait(false) ?? [])
                    .Where(mail => !mail.IsLocalDraft));
                vanished.Clear();
                changedFlags.Clear();
                usedQResync = false;
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
            }
            else if (folder.HighestKnownUid < uint.MaxValue && (!uidBoundary.HasValue || uidBoundary.Value > folder.HighestKnownUid))
            {
                var range = new UniqueIdRange(new UniqueId(folder.HighestKnownUid + 1),
                    uidBoundary.HasValue ? new UniqueId(uidBoundary.Value) : UniqueId.MaxValue);
                discovered = await remote.SearchAsync(SearchQuery.Uids(range).And(SearchQuery.NotDeleted), cancellationToken).ConfigureAwait(false);
            }

            var missingUids = discovered.Where(uid => !known.ContainsKey(uid.Id)).ToList();
            downloaded.AddRange(await DownloadMessagesByUidsAsync(client, remote, folder, missingUids,
                synchronizer, cancellationToken, suppressMatchingLocalFilters).ConfigureAwait(false));

            var membershipReconciled = known.Count == 0;
            if (known.Count > 0)
            {
                if (usedQResync && canUseDelta)
                {
                    await ApplyFlagChangesAsync(folder, changedFlags, known).ConfigureAwait(false);
                    await ApplyDeletedUidsAsync(folder, vanished.ToList()).ConfigureAwait(false);
                }
                else
                {
                    var flagsRequest = new FetchRequest(_existingMailSynchronizationFlags);
                    if (strategy != ImapSyncStrategy.UidBased && canUseDelta)
                        flagsRequest.ChangedSince = (ulong)folder.HighestModeSeq;

                    // A compact range avoids thousands of UID arguments and repeated SEARCH round trips.
                    var range = new UniqueIdRange(new UniqueId(known.Keys.Min()), new UniqueId(known.Keys.Max()));
                    IList<IMessageSummary> summaries;
                    try
                    {
                        summaries = await remote.FetchAsync(range, flagsRequest, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (flagsRequest.ChangedSince.HasValue && IsExtensionRejection(ex) && client.IsConnected)
                    {
                        strategy = ImapSyncStrategy.UidBased;
                        _mailboxFallbacks[folder.Id] = strategy;
                        flagsRequest.ChangedSince = null;
                        summaries = await remote.FetchAsync(range, flagsRequest, cancellationToken).ConfigureAwait(false);
                    }

                    if (summaries.Any(summary => summary.UniqueId.IsValid && !summary.Flags.HasValue))
                        throw new IOException("IMAP flag reconciliation returned incomplete flags.");

                    await ApplySummaryFlagUpdatesAsync(known, summaries).ConfigureAwait(false);
                    await ApplyDeletedUidsAsync(folder, summaries.Where(summary => summary.Flags?.HasFlag(MessageFlags.Deleted) == true)
                        .Select(summary => summary.UniqueId).ToList()).ConfigureAwait(false);

                    membershipReconciled = true;
                    if (!flagsRequest.ChangedSince.HasValue)
                    {
                        // Only a successful full FLAGS response establishes absence. A delta never does.
                        var returned = summaries.Select(summary => summary.UniqueId.Id).ToHashSet();
                        await ApplyDeletedUidsAsync(folder, known.Keys.Where(uid => !returned.Contains(uid))
                            .Select(uid => new UniqueId(uid)).ToList()).ConfigureAwait(false);
                    }
                    else
                    {
                        // CONDSTORE does not report vanished UIDs. Reconcile membership on each sync.
                        await ReconcileDeletedMessagesAsync(folder, remote, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (repair)
            {
                // Include cached messages outside the configured initial range in deletion checks.
                if (!membershipReconciled)
                    await ReconcileDeletedMessagesAsync(folder, remote, cancellationToken).ConfigureAwait(false);
                folder.LastUidReconcileUtc = DateTime.UtcNow;
            }

            var observedMax = discovered.Count > 0 ? discovered.Max(uid => uid.Id) : 0;
            folder.HighestKnownUid = Math.Max(folder.HighestKnownUid, uidBoundary ?? observedMax);
            folder.HighestModeSeq = strategy != ImapSyncStrategy.UidBased && supportsModSeq && modSeqBoundary <= long.MaxValue
                ? (long)modSeqBoundary : 0;
            return downloaded;
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
        if (remoteFolder.UidValidity == 0)
            throw new IOException("IMAP server did not provide a valid UIDVALIDITY.");

        if (folder.UidValidity != 0 && remoteFolder.UidValidity != folder.UidValidity)
        {
            _logger.Warning("UIDVALIDITY changed for folder {FolderName}. Resetting local folder state.", folder.FolderName);

            var existingMails = await _mailService.GetMailsByFolderIdAsync(folder.Id).ConfigureAwait(false);
            await _mailService.DeleteMailsAsync(folder.MailAccountId,
                existingMails.Where(mail => !mail.IsLocalDraft).Select(mail => mail.Id)).ConfigureAwait(false);

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

    private async Task ApplyDeletedUidsAsync(MailItemFolder folder, IList<UniqueId> uniqueIds)
    {
        if (uniqueIds == null || uniqueIds.Count == 0)
            return;

        var existingMails = await _mailService.GetExistingMailsAsync(folder.Id, uniqueIds.Distinct()).ConfigureAwait(false);
        await _mailService.DeleteMailsAsync(folder.MailAccountId,
            existingMails.Where(mail => !mail.IsLocalDraft).Select(mail => mail.Id)).ConfigureAwait(false);
    }

    private async Task ApplyFlagChangesAsync(MailItemFolder folder, IDictionary<uint, MessageFlags> changedFlags,
        IReadOnlyDictionary<uint, MailCopy> known)
    {
        var updates = new List<MailCopyStateUpdate>();
        var deleted = new List<UniqueId>();
        foreach (var (uid, flags) in changedFlags)
        {
            if (!known.TryGetValue(uid, out var mail))
                continue;

            if (flags.HasFlag(MessageFlags.Deleted))
                deleted.Add(new UniqueId(uid));
            else if (CreateMailStateUpdate(mail, flags) is { } update)
                updates.Add(update);
        }

        if (updates.Count > 0)
            await _mailService.ApplyMailStateUpdatesAsync(updates).ConfigureAwait(false);
        await ApplyDeletedUidsAsync(folder, deleted).ConfigureAwait(false);
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
            return;

        var remoteAllUids = await remoteFolder.SearchAsync(SearchQuery.NotDeleted, cancellationToken).ConfigureAwait(false);
        var deletedUids = allLocalUids.Except(remoteAllUids).ToList();

        await ApplyDeletedUidsAsync(localFolder, deletedUids).ConfigureAwait(false);
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
