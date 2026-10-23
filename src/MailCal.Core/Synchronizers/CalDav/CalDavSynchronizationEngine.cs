#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Itenso.TimePeriod;
using Serilog;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Extensions;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Core.Domain.Models.CardDav;
using MailCal.Core.Domain.Models.Synchronization;
using MailCal.Core.Integration.Processors;
using MailCal.Core.Misc;
using MailCal.Services;

namespace MailCal.Core.Synchronizers.CalDav;

/// <summary>
/// Synchronizes the CalDAV calendars of an IMAP account the way Apple Calendar does: a calendar
/// whose sync token and ctag did not change is skipped; otherwise only the resources that changed
/// are downloaded. Changes come from sync-collection (RFC 6578) where the server supports it, or
/// from an ETag-only calendar-query compared with the stored ICS shadows. Changed resources are
/// fetched with calendar-multiget (or GET), expanded for the synchronization window and stored.
/// When the window moves, unchanged resources are expanded again from their stored ICS files.
/// </summary>
/// <remarks>
/// Fallback ladder per calendar: sync-collection delta → ETag listing (calendar-query) →
/// full-window calendar-query with calendar data. Downloads: calendar-multiget → GET.
/// Server capabilities, refusals and sync checkpoints are kept in <see cref="ICalDavStateStore"/>.
/// </remarks>
public sealed partial class CalDavSynchronizationEngine : ICalDavSynchronizationEngine
{
    internal const int DownloadBatchSize = 50;
    private const int ConcurrentDownloads = 4;
    private const int MaximumSyncPages = 100;

    /// <summary>How long a discovered principal and calendar home are used before discovery runs again.</summary>
    internal static readonly TimeSpan DiscoveryLifetime = TimeSpan.FromHours(24);

    private static readonly ILogger Logger = Log.ForContext<CalDavSynchronizationEngine>();

    private readonly ICalDavClient _client;
    private readonly IImapChangeProcessor _changeProcessor;
    private readonly ICalendarService _calendarService;
    private readonly IAutoDiscoveryService _autoDiscoveryService;
    private readonly ICalDavStateStore _stateStore;

    public CalDavSynchronizationEngine(
        ICalDavClient client,
        IImapChangeProcessor changeProcessor,
        ICalendarService calendarService,
        IAutoDiscoveryService autoDiscoveryService,
        ICalDavStateStore? stateStore = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _changeProcessor = changeProcessor ?? throw new ArgumentNullException(nameof(changeProcessor));
        _calendarService = calendarService ?? throw new ArgumentNullException(nameof(calendarService));
        _autoDiscoveryService = autoDiscoveryService ?? throw new ArgumentNullException(nameof(autoDiscoveryService));
        _stateStore = stateStore ?? new TransientCalDavStateStore();
    }

    #region Synchronization

    public async Task<CalendarSynchronizationResult> SynchronizeAsync(
        MailAccount account,
        CalendarSynchronizationOptions options,
        Action<int, int>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var calendarSource = account.GetEffectiveCalendarIntegrationSource();
        if (calendarSource == AccountIntegrationSource.Local)
            return CalendarSynchronizationResult.Empty;

        if (calendarSource != AccountIntegrationSource.Dav ||
            account.ProviderType != MailProviderType.IMAP4 ||
            !account.IsCalendarAccessGranted ||
            account.ServerInformation == null ||
            account.ServerInformation.CalendarSupportMode != ImapCalendarSupportMode.CalDav)
        {
            return CalendarSynchronizationResult.Failed(new InvalidOperationException(Translator.Synchronizer_CalendarUnavailable));
        }

        var serviceUri = await CalDavConnection.ResolveServiceUriAsync(account, _autoDiscoveryService, cancellationToken).ConfigureAwait(false);
        if (serviceUri == null)
        {
            Logger.Information("Skipping calendar sync for {AccountName}: CalDAV endpoint is not configured.", account.Name);
            return CalendarSynchronizationResult.Failed(new InvalidOperationException(Translator.Synchronizer_CalendarUnavailable));
        }

        var password = CalDavConnection.ResolvePassword(account);
        if (string.IsNullOrWhiteSpace(password))
        {
            Logger.Warning("Skipping calendar sync for {AccountName}: empty credentials.", account.Name);
            return CalendarSynchronizationResult.Failed(new InvalidOperationException(Translator.Synchronizer_CalendarUnavailable));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var username = CalDavConnection.ResolveUsername(account);
        if (string.IsNullOrWhiteSpace(username))
        {
            Logger.Warning("Skipping calendar sync for {AccountName}: account email address is empty for CalDAV credentials.", account.Name);
            return CalendarSynchronizationResult.Failed(new InvalidOperationException(Translator.Synchronizer_CalendarUnavailable));
        }

        var settings = new CalDavConnectionSettings
        {
            ServiceUri = serviceUri,
            Username = username,
            Password = password
        };

        var state = await _stateStore.GetAsync(account.Id, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CalDavCalendar> remoteCalendars;

        try
        {
            remoteCalendars = await RefreshCalendarsAsync(settings, state, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            Logger.Warning("Skipping calendar sync for {AccountName}: CalDAV authentication failed for username {Username}.", account.Name, username);
            return CalendarSynchronizationResult.Empty;
        }

        // Task lists (VTODO-only collections) are recorded in the state but not shown as calendars.
        var eventCalendars = remoteCalendars.Where(calendar => calendar.SupportsEvents).ToList();
        await SynchronizeCalendarMetadataAsync(account, eventCalendars).ConfigureAwait(false);
        await SaveStateAsync(account.Id, state, cancellationToken).ConfigureAwait(false);

        if (options?.Type == CalendarSynchronizationType.CalendarMetadata)
            return CalendarSynchronizationResult.Empty;

        var localCalendars = await _changeProcessor.GetAccountCalendarsAsync(account.Id).ConfigureAwait(false) ?? [];
        var remoteCalendarsById = eventCalendars
            .GroupBy(calendar => calendar.RemoteCalendarId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        if (options?.Type == CalendarSynchronizationType.SingleCalendar && options.SynchronizationCalendarIds?.Count > 0)
        {
            localCalendars = localCalendars
                .Where(calendar => options.SynchronizationCalendarIds.Contains(calendar.Id))
                .ToList();
        }

        localCalendars = localCalendars
            .Where(calendar => calendar.IsSynchronizationEnabled)
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var periodStartUtc = now.AddYears(-1);
        var periodEndUtc = now.AddYears(2);
        var forceSync = options?.Type is CalendarSynchronizationType.ExecuteRequests or CalendarSynchronizationType.SingleCalendar;
        var result = CalendarSynchronizationResult.Empty;

        var totalCalendars = localCalendars.Count;
        if (totalCalendars > 0)
            reportProgress?.Invoke(totalCalendars, totalCalendars);

        for (var i = 0; i < totalCalendars; i++)
        {
            var localCalendar = localCalendars[i];
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(localCalendar.RemoteCalendarId) ||
                !remoteCalendarsById.TryGetValue(localCalendar.RemoteCalendarId, out var remoteCalendar))
            {
                continue;
            }

            var context = new CalendarContext(
                account,
                settings,
                localCalendar,
                remoteCalendar,
                GetOrAddCalendarState(state, remoteCalendar),
                periodStartUtc,
                periodEndUtc,
                forceSync);

            try
            {
                await SynchronizeCalendarAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DavRequestException ex) when (ex.StatusCode == 401)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One calendar failing (a refused REPORT, an unreadable payload) must not stop the others.
                Logger.Warning(ex, "CalDAV synchronization of calendar {CalendarName} failed", localCalendar.Name);
                result.MergeIssues([Classify(ex, localCalendar)]);
            }

            await SaveStateAsync(account.Id, state, cancellationToken).ConfigureAwait(false);
            reportProgress?.Invoke(totalCalendars, totalCalendars - (i + 1));
        }

        return result;
    }

    /// <summary>
    /// Lists the calendar homes on every synchronization: one request shows added, renamed and
    /// removed calendars and, through sync token and ctag, which of them changed. The principal,
    /// homes and DAV header are resolved again only when unknown, older than
    /// <see cref="DiscoveryLifetime"/>, or when the stored home no longer answers.
    /// </summary>
    private async Task<IReadOnlyList<CalDavCalendar>> RefreshCalendarsAsync(
        CalDavConnectionSettings settings,
        CalDavAccountState state,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CalDavCalendar>? calendars = null;

        if (state.HasDiscovery && state.DiscoveredAtUtc > DateTime.UtcNow - DiscoveryLifetime)
        {
            try
            {
                calendars = await _client.ListCalendarsAsync(settings, state.CalendarHomeHrefs, cancellationToken).ConfigureAwait(false);

                // An empty listing would remove every local calendar; confirm it with a full discovery.
                if (calendars == null || !calendars.Any(calendar => calendar.SupportsEvents))
                    calendars = null;
            }
            catch (DavRequestException ex) when (ex.StatusCode is > 0 and < 500 and not (401 or 429))
            {
                Logger.Information("CalDAV calendar home no longer answers ({StatusCode}); rediscovering", ex.StatusCode);
            }
        }

        if (calendars == null)
        {
            var discovery = await _client.DiscoverAccountAsync(settings, cancellationToken).ConfigureAwait(false)
                            ?? throw new InvalidOperationException(Translator.DavError_InvalidResponse);

            ApplyDiscovery(state, discovery);
            calendars = discovery.Calendars ?? Array.Empty<CalDavCalendar>();
        }

        ApplyCalendars(state, calendars);
        return calendars;
    }

    private static void ApplyDiscovery(CalDavAccountState state, CalDavAccountDiscovery discovery)
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
    }

    /// <summary>
    /// Records what the server reports for each collection. Sync checkpoints and remembered
    /// refusals survive; collections the server no longer lists are dropped.
    /// </summary>
    private static void ApplyCalendars(CalDavAccountState state, IReadOnlyList<CalDavCalendar> calendars)
    {
        var listedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var calendar in calendars)
        {
            listedIds.Add(calendar.RemoteCalendarId);
            var calendarState = GetOrAddCalendarState(state, calendar);

            calendarState.DisplayName = calendar.Name ?? string.Empty;
            calendarState.SupportedComponents = calendar.SupportedComponents?.ToList() ?? [];
            calendarState.SupportedReports = calendar.SupportedReports?.ToList() ?? [];
            calendarState.SupportsSyncCollection = calendar.SupportsSyncCollection;
            calendarState.SupportsMultiget = calendar.SupportsMultiget;
            calendarState.IsReadOnly = calendar.IsReadOnly;
            calendarState.QuotaAvailableBytes = calendar.QuotaAvailableBytes;
            calendarState.QuotaUsedBytes = calendar.QuotaUsedBytes;
            calendarState.MaxResourceSize = calendar.MaxResourceSize;
            calendarState.MaxAttendeesPerInstance = calendar.MaxAttendeesPerInstance;
            calendarState.DefaultAlarmVEventDateTime = calendar.DefaultAlarmVEventDateTime ?? string.Empty;
            calendarState.DefaultAlarmVEventDate = calendar.DefaultAlarmVEventDate ?? string.Empty;
            calendarState.OwnerHref = calendar.OwnerHref ?? string.Empty;
        }

        foreach (var removed in state.Calendars.Where(pair => !listedIds.Contains(pair.Value.RemoteCalendarId)).Select(pair => pair.Key).ToList())
            state.Calendars.Remove(removed);
    }

    private static CalDavCalendarState GetOrAddCalendarState(CalDavAccountState state, CalDavCalendar calendar)
    {
        var calendarState = state.FindCalendar(calendar.RemoteCalendarId);
        if (calendarState != null)
            return calendarState;

        calendarState = new CalDavCalendarState { RemoteCalendarId = calendar.RemoteCalendarId };
        state.Calendars[calendar.RemoteCalendarId] = calendarState;
        return calendarState;
    }

    private async Task SynchronizeCalendarAsync(CalendarContext context, CancellationToken cancellationToken)
    {
        var localCalendar = context.LocalCalendar;
        var remoteCalendar = context.RemoteCalendar;
        var calendarState = context.CalendarState;

        // The stored token carries the window end it was produced for. Occurrences are only
        // materialized inside that window, so the calendar is re-anchored once the end gets close.
        var (storedProviderToken, storedWindowEndUtc) = CalendarSyncWindowToken.Decode(localCalendar.SynchronizationDeltaToken);
        var requiresReanchor = CalendarSyncWindowToken.RequiresReanchor(storedWindowEndUtc, DateTimeOffset.UtcNow);
        var windowEndUtc = !requiresReanchor && storedWindowEndUtc is { } storedEnd ? storedEnd : context.PeriodEndUtc;
        var serverToken = BuildServerToken(remoteCalendar);
        var remoteToken = string.IsNullOrWhiteSpace(serverToken) ? string.Empty : CalendarSyncWindowToken.Encode(serverToken, windowEndUtc);

        var isInitialSync = string.IsNullOrWhiteSpace(localCalendar.SynchronizationDeltaToken);
        var tokenChanged = !string.Equals(localCalendar.SynchronizationDeltaToken, remoteToken, StringComparison.Ordinal);

        if (!isInitialSync && !tokenChanged && !context.ForceSync)
        {
            Logger.Debug("CalDAV calendar {CalendarName} is unchanged", localCalendar.Name);
            return;
        }

        using var calendarLock = await CalDavConnection.LockCalendarAsync(localCalendar.Id, cancellationToken).ConfigureAwait(false);

        // Tokens stored before window anchoring existed carry no window. Those caches may hold
        // occurrence IDs from before they were preserved, so they are re-imported once, as is a
        // calendar without any token (first sync, or a server without ctag and sync token).
        var isLegacyToken = !isInitialSync && storedWindowEndUtc == null;
        var reimport = isInitialSync || isLegacyToken;
        var serverChanged = reimport || context.ForceSync || !string.Equals(storedProviderToken, serverToken, StringComparison.Ordinal);

        if (requiresReanchor && !isInitialSync)
            Logger.Information("Re-anchoring CalDAV calendar {CalendarName} to a new synchronization window ending {WindowEnd}.", localCalendar.Name, context.PeriodEndUtc);

        var shadows = await LoadShadowsAsync(context).ConfigureAwait(false);
        var changed = new List<CalDavResource>();
        var deletedKeys = new HashSet<string>(StringComparer.Ordinal);
        var nextSyncToken = calendarState.LastSyncToken;
        var nextTokenFromReport = calendarState.LastSyncTokenFromReport;

        if (serverChanged)
        {
            var delta = reimport ? null : await TryPullSyncCollectionDeltaAsync(context, cancellationToken).ConfigureAwait(false);
            if (delta != null)
            {
                foreach (var change in delta.Changes)
                {
                    if (change.IsDeleted)
                        deletedKeys.Add(ResourceKey(change.ExactHref));
                    else if (!IsCurrent(shadows, change))
                        changed.Add(change);
                }

                nextSyncToken = delta.NextSyncToken;
                nextTokenFromReport = true;
                Logger.Information("CalDAV delta for {CalendarName}: {Changed} changed, {Deleted} deleted",
                    localCalendar.Name, changed.Count, deletedKeys.Count);
            }
            else
            {
                IReadOnlyList<CalDavResource> listing;
                try
                {
                    listing = await _client.ListEventResourcesAsync(
                        context.Settings,
                        remoteCalendar,
                        context.PeriodStartUtc,
                        context.PeriodEndUtc,
                        cancellationToken).ConfigureAwait(false) ?? Array.Empty<CalDavResource>();
                }
                catch (DavRequestException ex) when (ex.IsUnsupportedRequest)
                {
                    Logger.Information("CalDAV server refused the ETag listing for {CalendarName} ({StatusCode}); downloading the whole window", localCalendar.Name, ex.StatusCode);
                    await SynchronizeWholeWindowAsync(context, shadows, cancellationToken).ConfigureAwait(false);
                    CompleteCalendar(context, remoteToken, remoteCalendar.SyncToken, nextTokenFromReport: false);
                    await _changeProcessor.UpdateAccountCalendarAsync(localCalendar).ConfigureAwait(false);
                    return;
                }

                var listedKeys = new HashSet<string>(listing.Select(resource => ResourceKey(resource.ExactHref)), StringComparer.Ordinal);
                foreach (var key in shadows.Keys.Where(key => !listedKeys.Contains(key)))
                    deletedKeys.Add(key);

                changed.AddRange(listing.Where(resource => reimport || !IsCurrent(shadows, resource)));

                // A collection's DAV:sync-token can start a later sync-collection (RFC 6578 section 4).
                nextSyncToken = remoteCalendar.SyncToken ?? string.Empty;
                nextTokenFromReport = false;
                Logger.Information("CalDAV listing for {CalendarName}: {Listed} resources, {Changed} to download, {Deleted} to remove",
                    localCalendar.Name, listing.Count, changed.Count, deletedKeys.Count);
            }
        }

        var changedKeys = new HashSet<string>(changed.Select(resource => ResourceKey(resource.ExactHref)), StringComparer.Ordinal);

        // Moving the window or re-importing expands unchanged resources again from their stored copy.
        var reexpandKeys = requiresReanchor || reimport
            ? shadows.Keys.Where(key => !changedKeys.Contains(key) && !deletedKeys.Contains(key)).ToList()
            : [];

        var expandedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await DownloadAndApplyAsync(context, shadows, changed, deletedKeys, expandedIds, cancellationToken).ConfigureAwait(false);
        await ReexpandStoredResourcesAsync(context, shadows, reexpandKeys, deletedKeys, expandedIds, cancellationToken).ConfigureAwait(false);
        await DeleteResourcesAsync(shadows, deletedKeys).ConfigureAwait(false);

        if (reimport)
            await ReconcileDeletedCalendarItemsAsync(localCalendar, context.PeriodStartUtc, context.PeriodEndUtc, expandedIds).ConfigureAwait(false);

        await PruneExpiredOccurrencesAsync(localCalendar, context.PeriodStartUtc).ConfigureAwait(false);

        CompleteCalendar(context, remoteToken, nextSyncToken, nextTokenFromReport);
        await _changeProcessor.UpdateAccountCalendarAsync(localCalendar).ConfigureAwait(false);
    }

    private static void CompleteCalendar(CalendarContext context, string remoteToken, string? nextSyncToken, bool nextTokenFromReport)
    {
        context.CalendarState.LastSyncToken = nextSyncToken ?? string.Empty;
        context.CalendarState.LastSyncTokenFromReport = nextTokenFromReport && !string.IsNullOrEmpty(nextSyncToken);
        context.CalendarState.LastCTag = context.RemoteCalendar.CTag ?? string.Empty;
        context.CalendarState.LastSuccessfulSyncUtc = DateTime.UtcNow;
        context.LocalCalendar.SynchronizationDeltaToken = remoteToken;
    }

    /// <summary>
    /// Asks for what changed since the stored token. Returns null when the caller must list the
    /// collection instead: the server refused the REPORT or the token, or answered without a token
    /// (Yahoo and Zimbra send an empty multistatus for REPORTs they do not implement).
    /// </summary>
    private async Task<SyncCollectionDelta?> TryPullSyncCollectionDeltaAsync(CalendarContext context, CancellationToken cancellationToken)
    {
        var calendarState = context.CalendarState;
        var token = calendarState.LastSyncToken;
        var calendarName = context.LocalCalendar.Name;

        if (!calendarState.CanUseSyncCollection || string.IsNullOrEmpty(token))
            return null;

        var changes = new Dictionary<string, CalDavResource>(StringComparer.Ordinal);

        try
        {
            CalDavSyncPage? page;
            var pageCount = 0;
            do
            {
                page = await _client.SyncCollectionAsync(context.Settings, context.RemoteCalendar, token, cancellationToken).ConfigureAwait(false);

                if (page == null || string.IsNullOrWhiteSpace(page.NextSyncToken))
                {
                    calendarState.SyncCollectionRefused = true;
                    Logger.Information("CalDAV server answered sync-collection for {CalendarName} without a sync token; using the ETag listing from now on", calendarName);
                    return null;
                }

                if (page.IsTruncated && string.Equals(page.NextSyncToken, token, StringComparison.Ordinal))
                {
                    Logger.Information("CalDAV sync-collection for {CalendarName} did not advance; listing the collection", calendarName);
                    return null;
                }

                // A later page describes a later state of the same resource.
                foreach (var change in page.Changes ?? Array.Empty<CalDavResource>())
                    changes[ResourceKey(change.ExactHref)] = change;

                token = page.NextSyncToken;
            } while (page.IsTruncated && ++pageCount < MaximumSyncPages);

            return new SyncCollectionDelta(changes.Values.ToList(), token);
        }
        catch (DavRequestException ex) when (ex.HasError("valid-sync-token") || (!ex.IsUnsupportedRequest && ex.StatusCode is 403 or 409 or 410 or 412))
        {
            // The server no longer knows the token (RFC 6578 section 3.2). What changed since is
            // unknown, so the collection is compared against a complete listing. A token that only
            // ever came from PROPFIND and is refused will be refused again.
            if (!calendarState.LastSyncTokenFromReport)
                calendarState.SyncCollectionRefused = true;

            Logger.Information("CalDAV sync token for {CalendarName} was refused ({StatusCode}); listing the collection", calendarName, ex.StatusCode);
            return null;
        }
        catch (DavRequestException ex) when (ex.IsUnsupportedRequest)
        {
            calendarState.SyncCollectionRefused = true;
            Logger.Information("CalDAV server refused sync-collection for {CalendarName} ({StatusCode}); using the ETag listing", calendarName, ex.StatusCode);
            return null;
        }
    }

    /// <summary>
    /// Downloads changed resources in batches and stores them. Resources that are gone by the time
    /// they are requested are treated as deleted.
    /// </summary>
    private async Task DownloadAndApplyAsync(
        CalendarContext context,
        Dictionary<string, ResourceShadow> shadows,
        IReadOnlyList<CalDavResource> changed,
        HashSet<string> deletedKeys,
        HashSet<string> expandedIds,
        CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < changed.Count; offset += DownloadBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = changed.Skip(offset).Take(DownloadBatchSize).ToList();
            var (downloaded, gone) = await DownloadAsync(context, batch, cancellationToken).ConfigureAwait(false);

            foreach (var resource in batch)
            {
                var key = ResourceKey(resource.ExactHref);
                if (gone.Contains(key))
                {
                    deletedKeys.Add(key);
                    continue;
                }

                if (!downloaded.TryGetValue(key, out var body))
                {
                    Logger.Warning("CalDAV resource {Href} was listed but returned no calendar data", resource.ExactHref);
                    continue;
                }

                var eTag = string.IsNullOrWhiteSpace(body.ETag) ? resource.ETag : body.ETag;
                await ApplyResourceAsync(context, shadows, resource.ExactHref, eTag, body.IcsContent, expandedIds).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Fetches resources with one calendar-multiget REPORT, then with GET for whatever the REPORT
    /// did not return: every resource when the server has no multiget, single ones when it
    /// answered without data. Also reports the resources the server no longer has.
    /// </summary>
    private async Task<(Dictionary<string, CalDavResource> Downloaded, HashSet<string> Gone)> DownloadAsync(
        CalendarContext context,
        IReadOnlyList<CalDavResource> resources,
        CancellationToken cancellationToken)
    {
        var downloaded = new Dictionary<string, CalDavResource>(StringComparer.Ordinal);
        var gone = new HashSet<string>(StringComparer.Ordinal);
        if (resources.Count == 0)
            return (downloaded, gone);

        var calendarState = context.CalendarState;

        if (calendarState.CanUseMultiget)
        {
            try
            {
                var hrefs = resources.Select(resource => resource.ExactHref).ToList();
                var answers = await _client.MultiGetEventsAsync(context.Settings, context.RemoteCalendar, hrefs, cancellationToken).ConfigureAwait(false);

                foreach (var answer in answers ?? Array.Empty<CalDavResource>())
                {
                    var key = ResourceKey(answer.ExactHref);
                    if (answer.IsDeleted)
                        gone.Add(key);
                    else if (!string.IsNullOrWhiteSpace(answer.IcsContent))
                        downloaded[key] = answer;
                }
            }
            catch (DavRequestException ex) when (ex.IsUnsupportedRequest)
            {
                calendarState.MultigetRefused = true;
                Logger.Information("CalDAV server refused calendar-multiget for {CalendarName} ({StatusCode}); using GET", context.LocalCalendar.Name, ex.StatusCode);
            }
        }

        var remaining = resources
            .Where(resource => !downloaded.ContainsKey(ResourceKey(resource.ExactHref)) && !gone.Contains(ResourceKey(resource.ExactHref)))
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
            var key = ResourceKey(resource.ExactHref);
            if (resource.IsDeleted)
                gone.Add(key);
            else if (!string.IsNullOrWhiteSpace(resource.IcsContent))
                downloaded[key] = resource;
        }

        return (downloaded, gone);
    }

    /// <summary>
    /// Expands unchanged resources from their stored ICS file for the current window. Resources
    /// whose file is missing are downloaded instead.
    /// </summary>
    private async Task ReexpandStoredResourcesAsync(
        CalendarContext context,
        Dictionary<string, ResourceShadow> shadows,
        IReadOnlyList<string> keys,
        HashSet<string> deletedKeys,
        HashSet<string> expandedIds,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
            return;

        var missing = new List<CalDavResource>();

        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!shadows.TryGetValue(key, out var shadow))
                continue;

            CalDavResourceSnapshot? snapshot = null;
            foreach (var item in shadow.Items)
            {
                snapshot = await _changeProcessor
                    .GetCalendarItemIcsAsync(context.Account.Id, context.LocalCalendar.Id, item.CalendarItemId)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(snapshot?.IcsContent))
                    break;
            }

            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.IcsContent))
            {
                missing.Add(new CalDavResource { ExactHref = shadow.ExactHref, ETag = shadow.ETag });
                continue;
            }

            await ApplyResourceAsync(context, shadows, shadow.ExactHref, shadow.ETag, snapshot.IcsContent, expandedIds).ConfigureAwait(false);
        }

        if (missing.Count > 0)
        {
            Logger.Information("CalDAV calendar {CalendarName}: {Count} stored resources have no ICS copy; downloading them", context.LocalCalendar.Name, missing.Count);
            await DownloadAndApplyAsync(context, shadows, missing, deletedKeys, expandedIds, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyResourceAsync(
        CalendarContext context,
        Dictionary<string, ResourceShadow> shadows,
        string exactHref,
        string eTag,
        string icsContent,
        HashSet<string> expandedIds)
    {
        var events = _client.ExpandCalendarResource(icsContent, exactHref, eTag, context.PeriodStartUtc, context.PeriodEndUtc)
                     ?? Array.Empty<CalDavCalendarEvent>();

        await ApplyExpandedEventsAsync(context, shadows, exactHref, eTag, icsContent, events, expandedIds).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores the events of one resource and removes the rows of that resource the new version no
    /// longer produces (a removed occurrence, an event moved out of the window).
    /// </summary>
    private async Task ApplyExpandedEventsAsync(
        CalendarContext context,
        Dictionary<string, ResourceShadow> shadows,
        string exactHref,
        string eTag,
        string icsContent,
        IReadOnlyList<CalDavCalendarEvent> events,
        HashSet<string> expandedIds)
    {
        var localCalendar = context.LocalCalendar;
        var resourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identity = await GetUserIdentityAsync(context.Account, CancellationToken.None).ConfigureAwait(false);

        foreach (var remoteEvent in events)
        {
            if (string.IsNullOrWhiteSpace(remoteEvent.RemoteEventId))
                continue;

            resourceIds.Add(remoteEvent.RemoteEventId);
            expandedIds.Add(remoteEvent.RemoteEventId);
            ApplyImportedResponseStatus(remoteEvent, identity);

            var existingLocalItem = await _changeProcessor
                .GetCalendarItemAsync(localCalendar.Id, remoteEvent.RemoteEventId)
                .ConfigureAwait(false);

            if (await ShouldSkipUnchangedEventAsync(context.Account, localCalendar, existingLocalItem, remoteEvent).ConfigureAwait(false))
                continue;

            await _changeProcessor
                .ManageCalendarEventAsync(remoteEvent, localCalendar, context.Account)
                .ConfigureAwait(false);

            var payload = string.IsNullOrWhiteSpace(icsContent) ? remoteEvent.IcsContent : icsContent;
            if (string.IsNullOrWhiteSpace(payload))
                continue;

            var localItem = existingLocalItem ?? await _changeProcessor
                .GetCalendarItemAsync(localCalendar.Id, remoteEvent.RemoteEventId)
                .ConfigureAwait(false);

            if (localItem == null)
                continue;

            await SeedDefaultRemindersAsync(context, existingLocalItem, localItem, remoteEvent, payload).ConfigureAwait(false);


            await _changeProcessor
                .SaveCalendarItemIcsAsync(
                    context.Account.Id,
                    localCalendar.Id,
                    localItem.Id,
                    remoteEvent.RemoteEventId,
                    exactHref,
                    eTag,
                    payload)
                .ConfigureAwait(false);
        }

        if (!shadows.TryGetValue(ResourceKey(exactHref), out var shadow))
            return;

        foreach (var item in shadow.Items)
        {
            if (string.IsNullOrWhiteSpace(item.RemoteEventId) || resourceIds.Contains(item.RemoteEventId))
                continue;

            await _changeProcessor.DeleteCalendarItemAsync(item.CalendarItemId).ConfigureAwait(false);
        }
    }

    private async Task DeleteResourcesAsync(Dictionary<string, ResourceShadow> shadows, IEnumerable<string> deletedKeys)
    {
        foreach (var key in deletedKeys)
        {
            if (!shadows.TryGetValue(key, out var shadow))
                continue;

            foreach (var item in shadow.Items)
                await _changeProcessor.DeleteCalendarItemAsync(item.CalendarItemId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Last resort for a server that refuses the ETag-only calendar-query: one calendar-query with
    /// calendar data for the whole window, compared event by event as before incremental sync.
    /// </summary>
    private async Task SynchronizeWholeWindowAsync(
        CalendarContext context,
        Dictionary<string, ResourceShadow> shadows,
        CancellationToken cancellationToken)
    {
        var remoteEvents = await _client.GetCalendarEventsAsync(
            context.Settings,
            context.RemoteCalendar,
            context.PeriodStartUtc,
            context.PeriodEndUtc,
            cancellationToken).ConfigureAwait(false) ?? Array.Empty<CalDavCalendarEvent>();

        var expandedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var resource in remoteEvents.GroupBy(remoteEvent => remoteEvent.RemoteResourceHref ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var first = resource.First();
            var events = resource
                .OrderByDescending(remoteEvent => remoteEvent.IsSeriesMaster)
                .ThenBy(remoteEvent => remoteEvent.Start)
                .ToList();

            await ApplyExpandedEventsAsync(context, shadows, resource.Key, first.ETag ?? string.Empty, first.IcsContent ?? string.Empty, events, expandedIds).ConfigureAwait(false);
        }

        await ReconcileDeletedCalendarItemsAsync(context.LocalCalendar, context.PeriodStartUtc, context.PeriodEndUtc, expandedIds).ConfigureAwait(false);
        await PruneExpiredOccurrencesAsync(context.LocalCalendar, context.PeriodStartUtc).ConfigureAwait(false);
    }

    private async Task<bool> ShouldSkipUnchangedEventAsync(
        MailAccount account,
        AccountCalendar localCalendar,
        CalendarItem? existingLocalItem,
        CalDavCalendarEvent remoteEvent)
    {
        if (existingLocalItem == null)
            return false;

        // Ensure unresolved parent-child linkage still gets corrected when required.
        if (!string.IsNullOrWhiteSpace(remoteEvent.SeriesMasterRemoteEventId) &&
            (existingLocalItem.RecurringCalendarItemId == null ||
             existingLocalItem.RecurringCalendarItemId == existingLocalItem.Id))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(remoteEvent.ETag))
            return false;

        var savedETag = await _changeProcessor
            .GetCalendarItemIcsETagAsync(account.Id, localCalendar.Id, existingLocalItem.Id)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(savedETag))
            return false;

        return string.Equals(savedETag.Trim(), remoteEvent.ETag.Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// After a complete re-import, removes rows in the window the server no longer produces,
    /// including rows that never had a stored ICS copy.
    /// </summary>
    private async Task ReconcileDeletedCalendarItemsAsync(
        AccountCalendar localCalendar,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        HashSet<string> remoteEventIds)
    {
        var syncPeriod = new TimeRange(periodStartUtc.UtcDateTime, periodEndUtc.UtcDateTime);
        var localEventsInWindow = await _calendarService
            .GetCalendarEventsAsync(localCalendar, syncPeriod)
            .ConfigureAwait(false) ?? [];

        foreach (var localEvent in localEventsInWindow)
        {
            if (string.IsNullOrWhiteSpace(localEvent.RemoteEventId) || remoteEventIds.Contains(localEvent.RemoteEventId))
                continue;

            await _changeProcessor.DeleteCalendarItemAsync(localEvent.Id).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Generated occurrences that fell out of the window are regenerated from the master on demand.
    /// Prune them so the table does not grow forever.
    /// </summary>
    private async Task PruneExpiredOccurrencesAsync(AccountCalendar localCalendar, DateTimeOffset periodStartUtc)
    {
        var expiredPeriod = new TimeRange(DateTime.MinValue, periodStartUtc.UtcDateTime);
        var expiredEvents = await _calendarService
            .GetCalendarEventsAsync(localCalendar, expiredPeriod)
            .ConfigureAwait(false) ?? [];

        foreach (var expiredEvent in expiredEvents)
        {
            if (!expiredEvent.IsRecurringChild || expiredEvent.LocalEndDate > expiredPeriod.End)
                continue;

            await _changeProcessor.DeleteCalendarItemAsync(expiredEvent.Id).ConfigureAwait(false);
        }
    }

    /// <summary>The calendar's stored resources, keyed by resource identity. Only server hrefs count.</summary>
    private async Task<Dictionary<string, ResourceShadow>> LoadShadowsAsync(CalendarContext context)
    {
        var rows = await _changeProcessor
            .GetCalendarIcsShadowsAsync(context.Account.Id, context.LocalCalendar.Id)
            .ConfigureAwait(false) ?? Array.Empty<CalendarIcsShadow>();

        return rows
            .Where(row => Uri.TryCreate(row.ExactHref, UriKind.Absolute, out var uri) &&
                          (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            .GroupBy(row => ResourceKey(row.ExactHref), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group =>
            {
                var items = group.ToList();
                var eTags = items.Select(item => item.ETag?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal).ToList();

                // Rows of one resource disagreeing on the ETag means an interrupted write; download it again.
                return new ResourceShadow(items[0].ExactHref, eTags.Count == 1 ? eTags[0] : string.Empty, items);
            }, StringComparer.Ordinal);
    }

    /// <summary>The version already stored locally, including resources this client just wrote.</summary>
    private static bool IsCurrent(Dictionary<string, ResourceShadow> shadows, CalDavResource remote)
        => !string.IsNullOrWhiteSpace(remote.ETag) &&
           shadows.TryGetValue(ResourceKey(remote.ExactHref), out var shadow) &&
           string.Equals(shadow.ETag, remote.ETag.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// Identity of a resource regardless of how a server spells its href: percent-encoding and
    /// the origin form both vary between listings, REPORT answers and stored metadata.
    /// </summary>
    internal static string ResourceKey(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return string.Empty;

        var value = href.Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/')
            : Uri.UnescapeDataString(value).TrimEnd('/');
    }

    /// <summary>
    /// The server state a calendar was synchronized against: "&lt;sync-token&gt;|&lt;ctag&gt;" or
    /// whichever exists. Empty for servers with neither, which keeps such calendars always synchronizing.
    /// </summary>
    internal static string BuildServerToken(CalDavCalendar calendar)
    {
        var syncToken = calendar.SyncToken?.Trim() ?? string.Empty;
        var ctag = calendar.CTag?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(syncToken) && !string.IsNullOrWhiteSpace(ctag))
            return $"{syncToken}|{ctag}";

        return !string.IsNullOrWhiteSpace(syncToken) ? syncToken : ctag;
    }

    private async Task SaveStateAsync(Guid accountId, CalDavAccountState state, CancellationToken cancellationToken)
    {
        try
        {
            await _stateStore.SaveAsync(accountId, state, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The state is a cache; losing a write costs a rediscovery, not data.
            Logger.Warning(ex, "Could not save the CalDAV state of account {AccountId}", accountId);
        }
    }

    private static SynchronizationIssue Classify(Exception exception, AccountCalendar calendar)
    {
        var issue = SynchronizationIssue.FromException(
            exception,
            "CalDavSynchronization",
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
            calendar.Name);

        if (issue != null)
        {
            issue.CalendarId = calendar.Id;
            issue.CalendarName = calendar.Name;
        }

        return issue ?? new SynchronizationIssue { Message = exception.Message, CalendarId = calendar.Id, CalendarName = calendar.Name };
    }

    /// <summary>
    /// Creates, renames, recolors and removes local calendars to match the server. Removing a
    /// calendar also removes its ICS store.
    /// </summary>
    internal async Task SynchronizeCalendarMetadataAsync(MailAccount account, IReadOnlyList<CalDavCalendar> remoteCalendars)
    {
        var localCalendars = await _changeProcessor.GetAccountCalendarsAsync(account.Id).ConfigureAwait(false) ?? [];
        var remoteCalendarsById = remoteCalendars
            .GroupBy(calendar => calendar.RemoteCalendarId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var usedCalendarColors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remotePrimaryCalendarId = GetPrimaryCalendarId(remoteCalendars);

        foreach (var localCalendar in localCalendars.ToList())
        {
            if (!string.IsNullOrWhiteSpace(localCalendar.RemoteCalendarId) && remoteCalendarsById.ContainsKey(localCalendar.RemoteCalendarId))
                continue;

            await _changeProcessor
                .DeleteCalendarIcsForCalendarAsync(account.Id, localCalendar.Id)
                .ConfigureAwait(false);
            await _changeProcessor.DeleteAccountCalendarAsync(localCalendar).ConfigureAwait(false);
            localCalendars.Remove(localCalendar);
        }

        foreach (var remoteCalendar in remoteCalendars)
        {
            var existingLocal = localCalendars.FirstOrDefault(calendar =>
                string.Equals(calendar.RemoteCalendarId, remoteCalendar.RemoteCalendarId, StringComparison.OrdinalIgnoreCase));

            var isPrimary = string.Equals(remoteCalendar.RemoteCalendarId, remotePrimaryCalendarId, StringComparison.OrdinalIgnoreCase);

            if (existingLocal == null)
            {
                var insertedCalendarColor = ResolveSynchronizedCalendarBackgroundColor(remoteCalendar.BackgroundColorHex, null, usedCalendarColors);
                var newCalendar = new AccountCalendar
                {
                    Id = Guid.NewGuid(),
                    AccountId = account.Id,
                    RemoteCalendarId = remoteCalendar.RemoteCalendarId,
                    Name = remoteCalendar.Name,
                    IsPrimary = isPrimary,
                    IsReadOnly = remoteCalendar.IsReadOnly,
                    IsSynchronizationEnabled = true,
                    IsExtended = true,
                    DefaultShowAs = remoteCalendar.DefaultShowAs,
                    BackgroundColorHex = insertedCalendarColor,
                    TimeZone = remoteCalendar.TimeZone,
                    SynchronizationDeltaToken = string.Empty
                };

                newCalendar.TextColorHex = ColorHelpers.GetReadableTextColorHex(newCalendar.BackgroundColorHex);
                usedCalendarColors.Add(newCalendar.BackgroundColorHex);
                await _changeProcessor.InsertAccountCalendarAsync(newCalendar).ConfigureAwait(false);
                continue;
            }

            var resolvedColor = ResolveSynchronizedCalendarBackgroundColor(remoteCalendar.BackgroundColorHex, existingLocal, usedCalendarColors);
            var resolvedTextColor = ColorHelpers.GetReadableTextColorHex(resolvedColor);
            var shouldUpdate = !string.Equals(existingLocal.Name, remoteCalendar.Name, StringComparison.Ordinal)
                               || !string.Equals(existingLocal.TimeZone, remoteCalendar.TimeZone, StringComparison.OrdinalIgnoreCase)
                               || existingLocal.IsReadOnly != remoteCalendar.IsReadOnly
                               || existingLocal.DefaultShowAs != remoteCalendar.DefaultShowAs
                               || existingLocal.IsPrimary != isPrimary
                               || !string.Equals(existingLocal.BackgroundColorHex, resolvedColor, StringComparison.OrdinalIgnoreCase)
                               || !string.Equals(existingLocal.TextColorHex, resolvedTextColor, StringComparison.OrdinalIgnoreCase);

            if (!shouldUpdate)
            {
                usedCalendarColors.Add(resolvedColor);
                continue;
            }

            existingLocal.Name = remoteCalendar.Name;
            existingLocal.TimeZone = remoteCalendar.TimeZone;
            existingLocal.IsReadOnly = remoteCalendar.IsReadOnly;
            existingLocal.DefaultShowAs = remoteCalendar.DefaultShowAs;
            existingLocal.IsPrimary = isPrimary;
            existingLocal.BackgroundColorHex = resolvedColor;
            existingLocal.TextColorHex = resolvedTextColor;
            usedCalendarColors.Add(existingLocal.BackgroundColorHex);
            await _changeProcessor.UpdateAccountCalendarAsync(existingLocal).ConfigureAwait(false);
        }
    }

    private static string GetPrimaryCalendarId(IReadOnlyList<CalDavCalendar> remoteCalendars)
    {
        if (remoteCalendars == null || remoteCalendars.Count == 0)
            return string.Empty;

        if (remoteCalendars.Any(calendar => calendar.Order.HasValue))
        {
            return remoteCalendars
                .OrderBy(calendar => calendar.Order ?? double.MaxValue)
                .ThenBy(calendar => calendar.Name, StringComparer.OrdinalIgnoreCase)
                .Select(calendar => calendar.RemoteCalendarId)
                .FirstOrDefault() ?? string.Empty;
        }

        return remoteCalendars[0].RemoteCalendarId;
    }

    private static string ResolveSynchronizedCalendarBackgroundColor(
        string remoteBackgroundColor,
        AccountCalendar? accountCalendar,
        ISet<string> usedCalendarColors)
    {
        if (accountCalendar?.IsBackgroundColorUserOverridden == true)
            return accountCalendar.BackgroundColorHex;

        var preferredColor = string.IsNullOrWhiteSpace(remoteBackgroundColor)
            ? accountCalendar?.BackgroundColorHex
            : remoteBackgroundColor;

        if (string.IsNullOrWhiteSpace(remoteBackgroundColor))
            return ColorHelpers.GetDistinctFlatColorHex(usedCalendarColors, preferredColor ?? string.Empty);

        return string.IsNullOrWhiteSpace(preferredColor)
            ? ColorHelpers.GenerateFlatColorHex()
            : preferredColor;
    }

    #endregion

    #region User changes

    public async Task CreateEventAsync(
        MailAccount account,
        CalendarItem item,
        List<CalendarEventAttendee>? attendees,
        List<Reminder>? reminders,
        IReadOnlyList<CalendarEventComposeAttachmentDraft>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        reminders = await ApplyDefaultAlarmOnCreateAsync(account, item, reminders, cancellationToken).ConfigureAwait(false);
        await UpsertEventAsync(account, item, attendees, cancellationToken).ConfigureAwait(false);
        await _changeProcessor.PersistCreatedCalendarEventAsync(
            item,
            attendees ?? [],
            reminders ?? [],
            item.RemoteEventId).ConfigureAwait(false);
        await ApplyAttachmentChangesAsync(account, item, attachments, isNewEvent: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateEventAsync(
        MailAccount account,
        CalendarItem item,
        List<CalendarEventAttendee>? attendees,
        IReadOnlyList<CalendarEventComposeAttachmentDraft>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        await UpsertEventAsync(account, item, attendees, cancellationToken).ConfigureAwait(false);
        await ApplyAttachmentChangesAsync(account, item, attachments, isNewEvent: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task RespondToEventAsync(
        MailAccount account,
        CalendarItem item,
        CalendarItemStatus response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        // An attendee answers with its own PARTSTAT (RFC 6638); only the organizer changes STATUS.
        if (await TryRespondAsAttendeeAsync(account, item, response, cancellationToken).ConfigureAwait(false))
            return;

        item.Status = response;
        await UpsertEventAsync(account, item, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteEventAsync(MailAccount account, CalendarItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(item);

        var (connection, calendar) = await ResolveWriteContextAsync(account, item.CalendarId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(item.RemoteEventId))
            throw new InvalidOperationException("Cannot delete CalDAV event because remote event ID is missing.");

        using var calendarLock = await CalDavConnection.LockCalendarAsync(item.CalendarId, cancellationToken).ConfigureAwait(false);
        var snapshot = await RequireResourceSnapshotAsync(account, item).ConfigureAwait(false);

        if (item.IsRecurringChild)
        {
            var icsContent = CalDavIcsMutator.RemoveOccurrence(snapshot.IcsContent, item.RemoteEventId);
            var result = await _client.UpsertCalendarEventAsync(
                connection,
                calendar,
                new CalDavWriteRequest
                {
                    RemoteEventId = item.RemoteEventId.GetProviderRemoteEventId(),
                    ExactHref = snapshot.ExactHref,
                    ETag = snapshot.ETag,
                    IcsContent = icsContent
                },
                cancellationToken).ConfigureAwait(false);

            await SaveSeriesSnapshotAsync(account, item, result.ExactHref, result.ETag, icsContent).ConfigureAwait(false);
            return;
        }

        await _client
            .DeleteCalendarEventAsync(connection, snapshot.ExactHref, snapshot.ETag, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task UpsertEventAsync(
        MailAccount account,
        CalendarItem item,
        List<CalendarEventAttendee>? attendees,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(item);

        CalendarIcsBuilder.EnsureCalendarItemDefaults(item, account, "caldav");
        var identity = await ApplyUserIdentityAsync(account, item, cancellationToken).ConfigureAwait(false);
        attendees ??= await _calendarService.GetAttendeesAsync(item.Id).ConfigureAwait(false) ?? [];

        var (connection, calendar) = await ResolveWriteContextAsync(account, item.CalendarId, cancellationToken).ConfigureAwait(false);
        using var calendarLock = await CalDavConnection.LockCalendarAsync(item.CalendarId, cancellationToken).ConfigureAwait(false);

        var snapshot = await _changeProcessor
            .GetCalendarItemIcsAsync(account.Id, item.CalendarId, item.Id)
            .ConfigureAwait(false);

        if (snapshot != null && (string.IsNullOrWhiteSpace(snapshot.ExactHref) || string.IsNullOrWhiteSpace(snapshot.ETag)))
        {
            throw new InvalidOperationException(
                "Cannot safely update the CalDAV event because its exact resource href or ETag is unavailable. Synchronize the calendar and retry.");
        }

        var icsContent = snapshot == null
            ? CalDavIcsMutator.Normalize(CalendarIcsBuilder.BuildIcsContent(item, attendees))
            : CalDavIcsMutator.UpdateEvent(snapshot.IcsContent, item, attendees, identity);

        icsContent = await PrepareOutgoingIcsAsync(account, item, calendar, snapshot, icsContent, cancellationToken).ConfigureAwait(false);

        var result = await _client
            .UpsertCalendarEventAsync(connection, calendar, new CalDavWriteRequest
            {
                RemoteEventId = item.RemoteEventId.GetProviderRemoteEventId(),
                ExactHref = snapshot?.ExactHref ?? string.Empty,
                ETag = snapshot?.ETag ?? string.Empty,
                IcsContent = icsContent,
                CreateOnly = snapshot == null
            }, cancellationToken)
            .ConfigureAwait(false);

        await RecordQuotaUsageAsync(account, calendar, snapshot, icsContent, cancellationToken).ConfigureAwait(false);
        await SaveSeriesSnapshotAsync(account, item, result.ExactHref, result.ETag, icsContent).ConfigureAwait(false);
    }

    /// <summary>
    /// A recurring series is one CalDAV resource shared by the master row and every occurrence row.
    /// After any write, every row of the series must hold the new ETag, otherwise the next write
    /// on a sibling sends a stale If-Match and the server answers 412.
    /// </summary>
    private async Task SaveSeriesSnapshotAsync(MailAccount account, CalendarItem item, string exactHref, string eTag, string icsContent)
    {
        var seriesParentId = item.IsRecurringChild
            ? item.RecurringCalendarItemId
            : item.IsRecurringParent ? item.Id : null;

        var savedItemIds = new HashSet<Guid>();

        if (seriesParentId is { } parentId)
        {
            var parent = await _calendarService.GetCalendarItemAsync(parentId).ConfigureAwait(false);
            if (parent != null && savedItemIds.Add(parent.Id))
            {
                await _changeProcessor.SaveCalendarItemIcsAsync(account.Id, item.CalendarId, parent.Id, parent.RemoteEventId, exactHref, eTag, icsContent).ConfigureAwait(false);
            }

            var children = await _calendarService.GetRecurringChildrenAsync(parentId).ConfigureAwait(false);
            foreach (var child in children ?? [])
            {
                if (!savedItemIds.Add(child.Id))
                    continue;

                await _changeProcessor.SaveCalendarItemIcsAsync(account.Id, item.CalendarId, child.Id, child.RemoteEventId, exactHref, eTag, icsContent).ConfigureAwait(false);
            }
        }

        if (savedItemIds.Add(item.Id))
        {
            await _changeProcessor.SaveCalendarItemIcsAsync(account.Id, item.CalendarId, item.Id, item.RemoteEventId, exactHref, eTag, icsContent).ConfigureAwait(false);
        }
    }

    private async Task<CalDavResourceSnapshot> RequireResourceSnapshotAsync(MailAccount account, CalendarItem item)
    {
        var snapshot = await _changeProcessor
            .GetCalendarItemIcsAsync(account.Id, item.CalendarId, item.Id)
            .ConfigureAwait(false);

        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.ExactHref) || string.IsNullOrWhiteSpace(snapshot.ETag))
        {
            throw new InvalidOperationException(
                "Cannot safely modify the CalDAV event because its exact resource href or ETag is unavailable. Synchronize the calendar and retry.");
        }

        return snapshot;
    }

    private async Task<(CalDavConnectionSettings Connection, CalDavCalendar Calendar)> ResolveWriteContextAsync(
        MailAccount account,
        Guid calendarId,
        CancellationToken cancellationToken)
    {
        var assignedCalendar = await _calendarService.GetAccountCalendarAsync(calendarId).ConfigureAwait(false);
        if (assignedCalendar == null || string.IsNullOrWhiteSpace(assignedCalendar.RemoteCalendarId))
            throw new InvalidOperationException("Cannot execute CalDAV operation because the target calendar has no remote ID.");

        var connection = await CalDavConnection.CreateRequiredSettingsAsync(account, _autoDiscoveryService, cancellationToken).ConfigureAwait(false);

        return (connection, new CalDavCalendar
        {
            RemoteCalendarId = assignedCalendar.RemoteCalendarId,
            Name = assignedCalendar.Name
        });
    }

    #endregion

    private sealed record CalendarContext(
        MailAccount Account,
        CalDavConnectionSettings Settings,
        AccountCalendar LocalCalendar,
        CalDavCalendar RemoteCalendar,
        CalDavCalendarState CalendarState,
        DateTimeOffset PeriodStartUtc,
        DateTimeOffset PeriodEndUtc,
        bool ForceSync);

    private sealed record ResourceShadow(string ExactHref, string ETag, List<CalendarIcsShadow> Items);

    private sealed record SyncCollectionDelta(IReadOnlyList<CalDavResource> Changes, string NextSyncToken);

    /// <summary>Keeps state for the engine's lifetime when no persistent store is available.</summary>
    private sealed class TransientCalDavStateStore : ICalDavStateStore
    {
        private readonly Dictionary<Guid, CalDavAccountState> _states = [];
        private readonly object _gate = new();

        public Task<CalDavAccountState> GetAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_states.TryGetValue(accountId, out var state))
                {
                    state = new CalDavAccountState();
                    _states[accountId] = state;
                }

                return Task.FromResult(state);
            }
        }

        public Task SaveAsync(Guid accountId, CalDavAccountState state, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                _states[accountId] = state;

            return Task.CompletedTask;
        }

        public async Task UpdateAsync(Guid accountId, Action<CalDavAccountState> update, CancellationToken cancellationToken = default)
            => update(await GetAsync(accountId, cancellationToken).ConfigureAwait(false));

        public Task DeleteAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                _states.Remove(accountId);

            return Task.CompletedTask;
        }
    }
}
