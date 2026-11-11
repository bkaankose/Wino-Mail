#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Misc;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.CardDav;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Domain.Models.Tasks;

namespace Wino.Core.Synchronizers.CalDav;

/// <summary>
/// Synchronizes the VTODO collections of an IMAP account's CalDAV server with its task lists.
/// Every collection that accepts VTODO is a task list; a VTODO whose RELATED-TO names a task of
/// the same list is a step of it (one level). A list whose ctag did not change is skipped;
/// otherwise changes come from sync-collection or an ETag listing compared with the stored
/// resources, and only changed resources are downloaded.
/// </summary>
/// <remarks>
/// Per-list checkpoints live on the list row, not in <see cref="ICalDavStateStore"/>, because the
/// calendar engine owns the per-calendar entries of that file and may write it concurrently:
/// <see cref="AccountTaskList.ListDeltaLink"/> is the server token ("sync-token|ctag") the list
/// was last synchronized against, <see cref="AccountTaskList.TaskDeltaLink"/> the sync-collection
/// token, and <see cref="AccountTaskList.DeltaLink"/> a set of flags (see <see cref="ListSyncFlags"/>).
/// </remarks>
public sealed partial class CalDavTaskSynchronizationEngine : ICalDavTaskSynchronizationEngine
{
    private const int ConcurrentDownloads = 4;
    private const int MaximumSyncPages = 100;

    private static readonly ILogger Logger = Log.ForContext<CalDavTaskSynchronizationEngine>();

    private readonly ICalDavClient _client;
    private readonly ITaskService _taskService;

    // VTODO resources reuse the calendar ICS store: folder = task list id, item = task or step id,
    // remote event id = VTODO UID. The store is path-based and keeps href, ETag and the ICS text,
    // so tasks get resource shadows without a database schema migration.
    private readonly ICalendarIcsFileService _icsFileService;

    private readonly IAutoDiscoveryService _autoDiscoveryService;
    private readonly ICalDavStateStore _stateStore;

    public CalDavTaskSynchronizationEngine(
        ICalDavClient client,
        ITaskService taskService,
        ICalendarIcsFileService icsFileService,
        IAutoDiscoveryService autoDiscoveryService,
        ICalDavStateStore stateStore)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _icsFileService = icsFileService ?? throw new ArgumentNullException(nameof(icsFileService));
        _autoDiscoveryService = autoDiscoveryService ?? throw new ArgumentNullException(nameof(autoDiscoveryService));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    #region Synchronization

    public async Task<TaskSynchronizationResult> SynchronizeAsync(
        MailAccount account,
        TaskSynchronizationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (!IsTaskSourceAvailable(account))
            return TaskSynchronizationResult.Failed(new InvalidOperationException(Translator.Synchronizer_TasksUnavailable));

        var settings = await CreateSettingsAsync(account, cancellationToken).ConfigureAwait(false);
        if (settings == null)
            return TaskSynchronizationResult.Failed(new InvalidOperationException(Translator.Synchronizer_TasksUnavailable));

        IReadOnlyList<CalDavCalendar> collections;
        try
        {
            collections = await RefreshCollectionsAsync(account.Id, settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DavRequestException { StatusCode: 401 })
        {
            Logger.Warning("Skipping task sync for {AccountName}: CalDAV authentication failed for username {Username}.", account.Name, settings.Username);
            return TaskSynchronizationResult.Empty;
        }

        var taskCollections = collections
            .Where(collection => collection.SupportsTasks && !string.IsNullOrWhiteSpace(collection.RemoteCalendarId))
            .GroupBy(collection => collection.RemoteCalendarId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        var lists = await SynchronizeTopologyAsync(account, taskCollections).ConfigureAwait(false);
        var collectionsById = taskCollections.ToDictionary(collection => collection.RemoteCalendarId, StringComparer.Ordinal);
        var synchronizationType = options?.Type ?? TaskSynchronizationType.Full;
        var issues = new List<SynchronizationIssue>();
        int downloaded = 0, changed = 0, deleted = 0;

        foreach (var list in lists)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (options?.TaskListId is Guid onlyListId && list.Id != onlyListId)
                continue;

            if (string.IsNullOrWhiteSpace(list.RemoteId) || !collectionsById.TryGetValue(list.RemoteId, out var collection))
                continue;

            try
            {
                var outcome = await SynchronizeListAsync(account, settings, list, collection, synchronizationType, cancellationToken).ConfigureAwait(false);
                downloaded += outcome.Downloaded;
                changed += outcome.Changed;
                deleted += outcome.Deleted;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DavRequestException ex) when (ex.StatusCode == 401)
            {
                throw;
            }
            catch (MailServerCertificateException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One list failing (a refused REPORT, an unreadable payload) must not stop the others.
                Logger.Warning(ex, "CalDAV synchronization of task list {ListName} failed", list.Title);
                issues.Add(Classify(ex, list));
            }
        }

        return TaskSynchronizationResult.Completed(downloaded, changed, deleted).MergeIssues(issues);
    }

    private static bool IsTaskSourceAvailable(MailAccount account)
        => account.TaskIntegrationSource == AccountIntegrationSource.Dav &&
           account.ProviderType == MailProviderType.IMAP4 &&
           account.IsTaskAccessGranted &&
           account.ServerInformation != null;

    /// <summary>The CalDAV endpoint is shared with the calendar, whether or not the calendar uses it.</summary>
    private async Task<CalDavConnectionSettings?> CreateSettingsAsync(MailAccount account, CancellationToken cancellationToken)
    {
        var serviceUri = await CalDavConnection.ResolveServiceUriAsync(account, _autoDiscoveryService, cancellationToken).ConfigureAwait(false);
        if (serviceUri == null)
        {
            Logger.Information("Skipping task sync for {AccountName}: CalDAV endpoint is not configured.", account.Name);
            return null;
        }

        var password = CalDavConnection.ResolvePassword(account);
        if (string.IsNullOrWhiteSpace(password))
        {
            Logger.Warning("Skipping task sync for {AccountName}: empty credentials.", account.Name);
            return null;
        }

        var username = CalDavConnection.ResolveUsername(account);
        if (string.IsNullOrWhiteSpace(username))
        {
            Logger.Warning("Skipping task sync for {AccountName}: account email address is empty for CalDAV credentials.", account.Name);
            return null;
        }

        return new CalDavConnectionSettings
        {
            ServiceUri = serviceUri,
            Username = username,
            Password = password,
            EndpointTrust = new DavEndpointTrust { AccountId = account.Id, Protocol = MailServerProtocol.CalDav }
        };
    }

    /// <summary>
    /// Lists the collections of the known calendar homes, or runs discovery when it is missing,
    /// stale, or the home no longer answers. An empty listing is confirmed with a discovery,
    /// because it would remove every task list.
    /// </summary>
    private async Task<IReadOnlyList<CalDavCalendar>> RefreshCollectionsAsync(
        Guid accountId,
        CalDavConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        var state = await _stateStore.GetAsync(accountId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CalDavCalendar>? collections = null;

        if (state.HasDiscovery && state.DiscoveredAtUtc > DateTime.UtcNow - CalDavSynchronizationEngine.DiscoveryLifetime)
        {
            try
            {
                collections = await _client.ListCalendarsAsync(settings, state.CalendarHomeHrefs, cancellationToken).ConfigureAwait(false);
                if (collections == null || collections.Count == 0)
                    collections = null;
            }
            catch (DavRequestException ex) when (ex.StatusCode is > 0 and < 500 and not (401 or 429))
            {
                Logger.Information("CalDAV calendar home no longer answers ({StatusCode}); rediscovering", ex.StatusCode);
            }
        }

        if (collections == null)
        {
            var discovery = await DiscoverAsync(accountId, settings, cancellationToken).ConfigureAwait(false);
            collections = discovery.Calendars ?? Array.Empty<CalDavCalendar>();
        }

        return collections;
    }

    /// <summary>
    /// Runs discovery and stores only its results. The per-calendar entries of the state belong to
    /// the calendar engine, which may be writing them at the same time.
    /// </summary>
    private async Task<CalDavAccountDiscovery> DiscoverAsync(Guid accountId, CalDavConnectionSettings settings, CancellationToken cancellationToken)
    {
        var discovery = await _client.DiscoverAccountAsync(settings, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException(Translator.DavError_InvalidResponse);

        try
        {
            await _stateStore.UpdateAsync(accountId, state =>
            {
                state.PrincipalHref = discovery.PrincipalHref ?? string.Empty;
                state.CalendarHomeHrefs = discovery.CalendarHomeHrefs?.ToList() ?? [];
                state.CalendarUserAddresses = discovery.CalendarUserAddresses?.ToList() ?? [];
                state.ScheduleInboxHref = discovery.ScheduleInboxHref ?? string.Empty;
                state.ScheduleOutboxHref = discovery.ScheduleOutboxHref ?? string.Empty;
                state.DavCapabilities = discovery.DavCapabilities?.ToList() ?? [];
                state.DefaultAlarmVEventDateTime = discovery.DefaultAlarmVEventDateTime ?? string.Empty;
                state.DefaultAlarmVEventDate = discovery.DefaultAlarmVEventDate ?? string.Empty;
                state.DiscoveredAtUtc = DateTime.UtcNow;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The state is a cache; losing a write costs a rediscovery, not data.
            Logger.Warning(ex, "Could not save the CalDAV discovery of account {AccountId}", accountId);
        }

        return discovery;
    }

    /// <summary>
    /// Mirrors the VTODO collections as task lists. Local lists of the account are untouched; a
    /// CalDAV list the server no longer has is removed together with its stored resources.
    /// </summary>
    private async Task<List<AccountTaskList>> SynchronizeTopologyAsync(MailAccount account, IReadOnlyList<CalDavCalendar> collections)
    {
        var allLists = await _taskService.GetTaskListsAsync().ConfigureAwait(false) ?? [];
        var before = allLists
            .Where(list => list.MailAccountId == account.Id && list.SourceKind == TaskSourceKind.CalDav)
            .ToList();
        var usedColors = allLists
            .Select(list => list.ColorHex)
            .Where(color => !string.IsNullOrWhiteSpace(color))
            .ToList();

        var incoming = new List<AccountTaskList>();
        foreach (var collection in collections)
        {
            var existing = before.FirstOrDefault(list => string.Equals(list.RemoteId, collection.RemoteCalendarId, StringComparison.Ordinal));
            var color = !string.IsNullOrWhiteSpace(collection.BackgroundColorHex) ? collection.BackgroundColorHex : existing?.ColorHex;
            if (string.IsNullOrWhiteSpace(color))
            {
                color = ColorPalette.GetDistinctColor(usedColors);
                usedColors.Add(color);
            }

            incoming.Add(new AccountTaskList
            {
                MailAccountId = account.Id,
                SourceKind = TaskSourceKind.CalDav,
                RemoteId = collection.RemoteCalendarId,
                Title = string.IsNullOrWhiteSpace(collection.Name) ? existing?.Title ?? collection.RemoteCalendarId : collection.Name,
                ColorHex = color,
                IsReadOnly = collection.IsReadOnly,
                HoldsCalendarEvents = collection.SupportsEvents,
                RemoteVersion = collection.CTag
            });
        }

        await _taskService.ApplyTaskTopologyDeltaAsync(new TaskTopologyDelta
        {
            MailAccountId = account.Id,
            SourceKind = TaskSourceKind.CalDav,
            Lists = incoming,
            ReconcileLists = true
        }).ConfigureAwait(false);

        var activeIds = new HashSet<string>(collections.Select(collection => collection.RemoteCalendarId), StringComparer.Ordinal);
        foreach (var removed in before.Where(list => !string.IsNullOrWhiteSpace(list.RemoteId) &&
                                                     !activeIds.Contains(list.RemoteId) &&
                                                     list.PendingMutation == TaskPendingMutation.None))
        {
            await _icsFileService.DeleteCalendarIcsForCalendarAsync(account.Id, removed.Id).ConfigureAwait(false);
        }

        // The topology delta does not recolor existing lists, so a color changed in another client is applied here.
        foreach (var existing in before.Where(list => list.PendingMutation == TaskPendingMutation.None))
        {
            var collection = collections.FirstOrDefault(item => string.Equals(item.RemoteCalendarId, existing.RemoteId, StringComparison.Ordinal));
            if (collection == null ||
                string.IsNullOrWhiteSpace(collection.BackgroundColorHex) ||
                string.Equals(collection.BackgroundColorHex, existing.ColorHex, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await _taskService.ApplyRemoteTaskListMetadataAsync(existing.Id, existing.GroupId, collection.BackgroundColorHex, null, null, null).ConfigureAwait(false);
        }

        return (await _taskService.GetTaskListsAsync(account.Id).ConfigureAwait(false) ?? [])
            .Where(list => list.SourceKind == TaskSourceKind.CalDav && !string.IsNullOrWhiteSpace(list.RemoteId))
            .ToList();
    }

    private async Task<ListOutcome> SynchronizeListAsync(
        MailAccount account,
        CalDavConnectionSettings settings,
        AccountTaskList list,
        CalDavCalendar collection,
        TaskSynchronizationType synchronizationType,
        CancellationToken cancellationToken)
    {
        var serverToken = CalDavSynchronizationEngine.BuildServerToken(collection);
        var isInitialSync = string.IsNullOrWhiteSpace(list.ListDeltaLink) && string.IsNullOrWhiteSpace(list.TaskDeltaLink);
        var isAuthoritative = synchronizationType is TaskSynchronizationType.Full or TaskSynchronizationType.Strict;

        if (!isInitialSync && !isAuthoritative &&
            !string.IsNullOrEmpty(serverToken) &&
            string.Equals(serverToken, list.ListDeltaLink, StringComparison.Ordinal))
        {
            Logger.Debug("CalDAV task list {ListName} is unchanged", list.Title);
            return default;
        }

        using var listLock = await CalDavConnection.LockCalendarAsync(list.Id, cancellationToken).ConfigureAwait(false);

        var flags = ListSyncFlags.Parse(list.DeltaLink);
        var context = new ListContext(account, settings, list, collection, flags);
        var shadows = await LoadShadowsAsync(account.Id, list.Id).ConfigureAwait(false);
        var changed = new List<CalDavResource>();
        var deletedKeys = new HashSet<string>(StringComparer.Ordinal);
        var listedKeys = new HashSet<string>(StringComparer.Ordinal);
        string nextSyncToken;
        bool isFullListing;

        var delta = isInitialSync || synchronizationType == TaskSynchronizationType.Strict
            ? null
            : await TryPullSyncCollectionDeltaAsync(context, cancellationToken).ConfigureAwait(false);

        if (delta != null)
        {
            foreach (var change in delta.Changes)
            {
                if (change.IsDeleted)
                    deletedKeys.Add(CalDavSynchronizationEngine.ResourceKey(change.ExactHref));
                else if (!IsCurrent(shadows, change))
                    changed.Add(change);
            }

            nextSyncToken = delta.NextSyncToken;
            flags.TokenFromReport = true;
            isFullListing = false;
            Logger.Information("CalDAV task delta for {ListName}: {Changed} changed, {Deleted} deleted", list.Title, changed.Count, deletedKeys.Count);
        }
        else
        {
            var listing = await _client.ListTaskResourcesAsync(settings, collection, cancellationToken).ConfigureAwait(false)
                          ?? Array.Empty<CalDavResource>();

            foreach (var resource in listing)
                listedKeys.Add(CalDavSynchronizationEngine.ResourceKey(resource.ExactHref));

            foreach (var key in shadows.Keys.Where(key => !listedKeys.Contains(key)))
                deletedKeys.Add(key);

            changed.AddRange(listing.Where(resource => !IsCurrent(shadows, resource)));

            // A collection's DAV:sync-token can start a later sync-collection (RFC 6578 section 4).
            nextSyncToken = collection.SyncToken ?? string.Empty;
            flags.TokenFromReport = false;
            isFullListing = true;
            Logger.Information("CalDAV task listing for {ListName}: {Listed} resources, {Changed} to download, {Deleted} to remove",
                list.Title, listing.Count, changed.Count, deletedKeys.Count);
        }

        var (applied, downloadedCount, incomplete) = await DownloadAsync(context, changed, deletedKeys, cancellationToken).ConfigureAwait(false);

        var storedTasks = await _taskService.GetTasksAsync(account.Id, list.Id).ConfigureAwait(false) ?? [];
        var deletedUids = ResolveDeletedUids(shadows, deletedKeys, applied);

        if (isFullListing && !incomplete)
            AddUidsMissingFromListing(storedTasks, shadows, listedKeys, applied, deletedUids);

        var plan = await BuildHierarchyAsync(account.Id, list, storedTasks, shadows, applied, deletedUids).ConfigureAwait(false);

        // A resource that was listed but not downloaded must be asked for again: the old token
        // and checkpoint make the next synchronization see it as changed once more.
        if (incomplete)
            nextSyncToken = list.TaskDeltaLink ?? string.Empty;

        await _taskService.ApplyTaskHierarchyDeltaAsync(new TaskHierarchyDelta
        {
            TaskListId = list.Id,
            Tasks = plan.Tasks,
            DeletedTaskRemoteIds = plan.DeletedTaskRemoteIds,
            DeletedStepRemoteIds = plan.DeletedStepRemoteIds,
            AuthoritativeStepParentRemoteIds = plan.AuthoritativeParentRemoteIds,
            TaskDeltaLink = nextSyncToken ?? string.Empty
        }).ConfigureAwait(false);

        await UpdateShadowsAsync(account.Id, list.Id, shadows, applied, deletedKeys, deletedUids).ConfigureAwait(false);
        await CommitCheckpointsAsync(list, incomplete ? null : serverToken, flags).ConfigureAwait(false);

        return new ListOutcome(downloadedCount, applied.Count(pair => pair.Value.IsDownloaded), plan.DeletedTaskRemoteIds.Count + plan.DeletedStepRemoteIds.Count);
    }

    /// <summary>Stores the flags, and the server token unless <paramref name="serverToken"/> is null.</summary>
    private async Task CommitCheckpointsAsync(AccountTaskList list, string? serverToken, ListSyncFlags flags)
    {
        if (serverToken != null)
            await _taskService.CommitTaskListDeltaLinkAsync(list.Id, serverToken).ConfigureAwait(false);

        var flagsText = flags.ToString();
        if (string.Equals(flagsText, list.DeltaLink ?? string.Empty, StringComparison.Ordinal))
            return;

        var current = await _taskService.GetTaskListAsync(list.Id).ConfigureAwait(false);
        if (current is not { PendingMutation: TaskPendingMutation.None })
            return;

        current.DeltaLink = flagsText;
        await _taskService.UpsertRemoteTaskListAsync(current).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks for what changed since the stored token. Returns null when the caller must list the
    /// collection instead: the server refused the REPORT or the token, or answered without a token.
    /// </summary>
    private async Task<SyncCollectionDelta?> TryPullSyncCollectionDeltaAsync(ListContext context, CancellationToken cancellationToken)
    {
        var flags = context.Flags;
        var token = context.List.TaskDeltaLink;
        var listName = context.List.Title;

        if (!context.Collection.SupportsSyncCollection || flags.SyncCollectionRefused || string.IsNullOrEmpty(token))
            return null;

        var changes = new Dictionary<string, CalDavResource>(StringComparer.Ordinal);

        try
        {
            CalDavSyncPage? page;
            var pageCount = 0;
            do
            {
                page = await _client.SyncCollectionAsync(context.Settings, context.Collection, token, cancellationToken).ConfigureAwait(false);

                if (page == null || string.IsNullOrWhiteSpace(page.NextSyncToken))
                {
                    flags.SyncCollectionRefused = true;
                    Logger.Information("CalDAV server answered sync-collection for {ListName} without a sync token; using the ETag listing from now on", listName);
                    return null;
                }

                if (page.IsTruncated && string.Equals(page.NextSyncToken, token, StringComparison.Ordinal))
                {
                    Logger.Information("CalDAV sync-collection for {ListName} did not advance; listing the collection", listName);
                    return null;
                }

                // A later page describes a later state of the same resource.
                foreach (var change in page.Changes ?? Array.Empty<CalDavResource>())
                    changes[CalDavSynchronizationEngine.ResourceKey(change.ExactHref)] = change;

                token = page.NextSyncToken;
            } while (page.IsTruncated && ++pageCount < MaximumSyncPages);

            return new SyncCollectionDelta(changes.Values.ToList(), token);
        }
        catch (DavRequestException ex) when (ex.HasError("valid-sync-token") || (!ex.IsUnsupportedRequest && ex.StatusCode is 403 or 409 or 410 or 412))
        {
            // A token that only ever came from PROPFIND and is refused will be refused again.
            if (!flags.TokenFromReport)
                flags.SyncCollectionRefused = true;

            Logger.Information("CalDAV sync token for {ListName} was refused ({StatusCode}); listing the collection", listName, ex.StatusCode);
            return null;
        }
        catch (DavRequestException ex) when (ex.IsUnsupportedRequest)
        {
            flags.SyncCollectionRefused = true;
            Logger.Information("CalDAV server refused sync-collection for {ListName} ({StatusCode}); using the ETag listing", listName, ex.StatusCode);
            return null;
        }
    }

    /// <summary>
    /// Downloads changed resources in batches with calendar-multiget, then GET for what the REPORT
    /// did not return. Resources that are gone are added to <paramref name="deletedKeys"/>; resources
    /// without a VTODO (events in a collection that holds both) are ignored.
    /// </summary>
    private async Task<(Dictionary<string, AppliedResource> Applied, int Downloaded, bool Incomplete)> DownloadAsync(
        ListContext context,
        IReadOnlyList<CalDavResource> changed,
        HashSet<string> deletedKeys,
        CancellationToken cancellationToken)
    {
        var applied = new Dictionary<string, AppliedResource>(StringComparer.Ordinal);
        var downloadedCount = 0;
        var incomplete = false;

        for (var offset = 0; offset < changed.Count; offset += CalDavSynchronizationEngine.DownloadBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = changed.Skip(offset).Take(CalDavSynchronizationEngine.DownloadBatchSize).ToList();
            var (downloaded, gone) = await DownloadBatchAsync(context, batch, cancellationToken).ConfigureAwait(false);

            foreach (var resource in batch)
            {
                var key = CalDavSynchronizationEngine.ResourceKey(resource.ExactHref);
                if (gone.Contains(key))
                {
                    deletedKeys.Add(key);
                    continue;
                }

                if (!downloaded.TryGetValue(key, out var body))
                {
                    incomplete = true;
                    Logger.Warning("CalDAV task resource {Href} was listed but returned no calendar data", resource.ExactHref);
                    continue;
                }

                downloadedCount++;
                var todo = TaskIcsBuilder.ParseMasterTodo(body.IcsContent);
                if (todo == null)
                    continue;

                var eTag = StrongETag(body.ETag);
                if (string.IsNullOrEmpty(eTag))
                    eTag = StrongETag(resource.ETag);

                applied[todo.Uid] = new AppliedResource(todo, resource.ExactHref, eTag, body.IcsContent, IsDownloaded: true);
            }
        }

        return (applied, downloadedCount, incomplete);
    }

    private async Task<(Dictionary<string, CalDavResource> Downloaded, HashSet<string> Gone)> DownloadBatchAsync(
        ListContext context,
        IReadOnlyList<CalDavResource> resources,
        CancellationToken cancellationToken)
    {
        var downloaded = new Dictionary<string, CalDavResource>(StringComparer.Ordinal);
        var gone = new HashSet<string>(StringComparer.Ordinal);
        if (resources.Count == 0)
            return (downloaded, gone);

        if (context.Collection.SupportsMultiget && !context.Flags.MultigetRefused)
        {
            try
            {
                var hrefs = resources.Select(resource => resource.ExactHref).ToList();
                var answers = await _client.MultiGetEventsAsync(context.Settings, context.Collection, hrefs, cancellationToken).ConfigureAwait(false);

                foreach (var answer in answers ?? Array.Empty<CalDavResource>())
                {
                    var key = CalDavSynchronizationEngine.ResourceKey(answer.ExactHref);
                    if (answer.IsDeleted)
                        gone.Add(key);
                    else if (!string.IsNullOrWhiteSpace(answer.IcsContent))
                        downloaded[key] = answer;
                }
            }
            catch (DavRequestException ex) when (ex.IsUnsupportedRequest)
            {
                context.Flags.MultigetRefused = true;
                Logger.Information("CalDAV server refused calendar-multiget for {ListName} ({StatusCode}); using GET", context.List.Title, ex.StatusCode);
            }
        }

        var remaining = resources
            .Where(resource => !downloaded.ContainsKey(CalDavSynchronizationEngine.ResourceKey(resource.ExactHref)) &&
                               !gone.Contains(CalDavSynchronizationEngine.ResourceKey(resource.ExactHref)))
            .Select(resource => resource.ExactHref)
            .ToList();

        if (remaining.Count == 0)
            return (downloaded, gone);

        using var throttle = new SemaphoreSlim(ConcurrentDownloads, ConcurrentDownloads);
        var fetched = await Task.WhenAll(remaining.Select(async href =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await _client.GetEventResourceAsync(context.Settings, href, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                throttle.Release();
            }
        })).ConfigureAwait(false);

        foreach (var resource in fetched.Where(value => value != null))
        {
            var key = CalDavSynchronizationEngine.ResourceKey(resource.ExactHref);
            if (resource.IsDeleted)
                gone.Add(key);
            else if (!string.IsNullOrWhiteSpace(resource.IcsContent))
                downloaded[key] = resource;
        }

        return (downloaded, gone);
    }

    /// <summary>
    /// UIDs of removed resources, and the old UID of a resource that now carries another one.
    /// A UID that a changed resource still carries is never removed.
    /// </summary>
    private static HashSet<string> ResolveDeletedUids(
        Dictionary<string, TaskResourceShadow> shadows,
        HashSet<string> deletedKeys,
        Dictionary<string, AppliedResource> applied)
    {
        var uids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var key in deletedKeys)
        {
            if (shadows.TryGetValue(key, out var shadow))
                uids.UnionWith(shadow.Items.Select(item => item.RemoteEventId).Where(uid => !string.IsNullOrWhiteSpace(uid)));
        }

        foreach (var resource in applied.Values)
        {
            if (shadows.TryGetValue(CalDavSynchronizationEngine.ResourceKey(resource.Href), out var shadow))
                uids.UnionWith(shadow.Items.Select(item => item.RemoteEventId).Where(uid => !string.IsNullOrWhiteSpace(uid) && uid != resource.Todo.Uid));
        }

        uids.ExceptWith(applied.Keys);
        return uids;
    }

    /// <summary>
    /// After a complete listing, a stored row whose UID no listed resource carries is gone even
    /// when no stored resource pointed at it (a shadow lost to an interrupted write).
    /// </summary>
    private static void AddUidsMissingFromListing(
        IReadOnlyList<AccountTask> storedTasks,
        Dictionary<string, TaskResourceShadow> shadows,
        HashSet<string> listedKeys,
        Dictionary<string, AppliedResource> applied,
        HashSet<string> deletedUids)
    {
        var present = new HashSet<string>(applied.Keys, StringComparer.Ordinal);
        foreach (var key in listedKeys)
        {
            if (shadows.TryGetValue(key, out var shadow))
                present.UnionWith(shadow.Items.Select(item => item.RemoteEventId).Where(uid => !string.IsNullOrWhiteSpace(uid)));
        }

        var storedUids = storedTasks
            .Select(task => task.RemoteId)
            .Concat(storedTasks.SelectMany(task => task.Steps ?? []).Select(step => step.RemoteId))
            .Where(uid => !string.IsNullOrWhiteSpace(uid));

        foreach (var uid in storedUids)
        {
            if (!present.Contains(uid))
                deletedUids.Add(uid);
        }
    }

    /// <summary>The list's stored resources, keyed by resource identity. Only server hrefs count.</summary>
    private async Task<Dictionary<string, TaskResourceShadow>> LoadShadowsAsync(Guid accountId, Guid listId)
    {
        var rows = await _icsFileService.GetCalendarIcsShadowsAsync(accountId, listId).ConfigureAwait(false)
                   ?? Array.Empty<CalendarIcsShadow>();

        return rows
            .Where(row => Uri.TryCreate(row.ExactHref, UriKind.Absolute, out var uri) &&
                          (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            .GroupBy(row => CalDavSynchronizationEngine.ResourceKey(row.ExactHref), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group =>
            {
                var items = group.ToList();
                var eTags = items.Select(item => item.ETag?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal).ToList();

                // Rows of one resource disagreeing on the ETag means an interrupted write; download it again.
                return new TaskResourceShadow(items[0].ExactHref, eTags.Count == 1 ? eTags[0] : string.Empty, items);
            }, StringComparer.Ordinal);
    }

    private static bool IsCurrent(Dictionary<string, TaskResourceShadow> shadows, CalDavResource remote)
        => !string.IsNullOrWhiteSpace(remote.ETag) &&
           shadows.TryGetValue(CalDavSynchronizationEngine.ResourceKey(remote.ExactHref), out var shadow) &&
           string.Equals(shadow.ETag, remote.ETag.Trim(), StringComparison.Ordinal);

    private static SynchronizationIssue Classify(Exception exception, AccountTaskList list)
    {
        var issue = SynchronizationIssue.FromException(
            exception,
            "CalDavTaskSynchronization",
            exception is DavRequestException { StatusCode: 403 } ? SynchronizerErrorSeverity.AuthRequired : SynchronizerErrorSeverity.Recoverable,
            exception switch
            {
                DavRequestException { StatusCode: 403 } => SynchronizerErrorCategory.Authentication,
                DavRequestException { StatusCode: 429 } => SynchronizerErrorCategory.RateLimit,
                DavRequestException { StatusCode: >= 500 } => SynchronizerErrorCategory.ServerError,
                HttpRequestException => SynchronizerErrorCategory.Network,
                FormatException => SynchronizerErrorCategory.Validation,
                _ => SynchronizerErrorCategory.ProtocolError
            },
            list.Title);

        return issue ?? new SynchronizationIssue { Message = exception.Message };
    }

    #endregion

    private sealed record ListContext(
        MailAccount Account,
        CalDavConnectionSettings Settings,
        AccountTaskList List,
        CalDavCalendar Collection,
        ListSyncFlags Flags);

    private readonly record struct ListOutcome(int Downloaded, int Changed, int Deleted);

    private sealed record SyncCollectionDelta(IReadOnlyList<CalDavResource> Changes, string NextSyncToken);

    private sealed record TaskResourceShadow(string ExactHref, string ETag, List<CalendarIcsShadow> Items);

    /// <summary>
    /// A VTODO to store. <paramref name="Href"/> is empty for a row rebuilt without a stored
    /// resource; such rows get no shadow and are downloaded again by the next listing.
    /// </summary>
    private sealed record AppliedResource(CalDavTodo Todo, string Href, string ETag, string IcsContent, bool IsDownloaded);

    /// <summary>
    /// What the server refused or how its token was obtained, kept in <see cref="AccountTaskList.DeltaLink"/>
    /// as ";"-separated words: "report" (the token came from a sync-collection REPORT, so a refusal
    /// means it expired), "no-sync-collection", "no-multiget" and "refused=yyyyMMdd", the day of the
    /// last refusal. Refusals expire after <see cref="RefusalLifetime"/>, so a server that refused
    /// once (or gained support later) is asked again; a flag without a day expires on the next read.
    /// </summary>
    private sealed class ListSyncFlags
    {
        private const string ReportToken = "report";
        private const string NoSyncCollection = "no-sync-collection";
        private const string NoMultiget = "no-multiget";
        private const string RefusedPrefix = "refused=";
        private const string DayFormat = "yyyyMMdd";

        private static readonly TimeSpan RefusalLifetime = TimeSpan.FromDays(7);

        private bool _syncCollectionRefused;
        private bool _multigetRefused;
        private DateTime? _refusedOnUtc;

        public bool TokenFromReport { get; set; }

        public bool SyncCollectionRefused
        {
            get => _syncCollectionRefused;
            set
            {
                _syncCollectionRefused = value;
                if (value)
                    _refusedOnUtc = DateTime.UtcNow.Date;
            }
        }

        public bool MultigetRefused
        {
            get => _multigetRefused;
            set
            {
                _multigetRefused = value;
                if (value)
                    _refusedOnUtc = DateTime.UtcNow.Date;
            }
        }

        public static ListSyncFlags Parse(string? value)
        {
            var words = new HashSet<string>(
                (value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

            var refusedWord = words.FirstOrDefault(word => word.StartsWith(RefusedPrefix, StringComparison.OrdinalIgnoreCase));
            DateTime? refusedOn = refusedWord != null &&
                                  DateTime.TryParseExact(refusedWord[RefusedPrefix.Length..], DayFormat, CultureInfo.InvariantCulture,
                                      DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)
                ? day
                : null;
            var isCurrent = refusedOn is { } date && DateTime.UtcNow - date < RefusalLifetime;

            return new ListSyncFlags
            {
                TokenFromReport = words.Contains(ReportToken),
                _syncCollectionRefused = isCurrent && words.Contains(NoSyncCollection),
                _multigetRefused = isCurrent && words.Contains(NoMultiget),
                _refusedOnUtc = isCurrent ? refusedOn : null
            };
        }

        public override string ToString()
        {
            var words = new List<string>(4);
            if (TokenFromReport)
                words.Add(ReportToken);
            if (SyncCollectionRefused)
                words.Add(NoSyncCollection);
            if (MultigetRefused)
                words.Add(NoMultiget);
            if ((SyncCollectionRefused || MultigetRefused) && _refusedOnUtc is { } refusedOn)
                words.Add(RefusedPrefix + refusedOn.ToString(DayFormat, CultureInfo.InvariantCulture));

            return string.Join(';', words);
        }
    }
}
