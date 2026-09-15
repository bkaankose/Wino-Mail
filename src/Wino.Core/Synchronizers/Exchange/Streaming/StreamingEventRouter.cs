using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Server;

namespace Wino.Core.Synchronizers.Exchange.Streaming;

/// <summary>A single coalesced streaming change: an item event in, or a folder event of, a remote folder.</summary>
/// <param name="RemoteFolderId">The EWS folder id (EWS streaming), or null when <paramref name="MapiFolderId"/> names the folder.</param>
/// <param name="IsFolderEvent">True for a hierarchy change (folder created, moved or deleted).</param>
/// <param name="MapiFolderId">The MAPI folder id as 16 hex digits (MAPI notifications).</param>
internal readonly record struct StreamingChange(string RemoteFolderId, bool IsFolderEvent, string MapiFolderId = null);

/// <summary>The sync requests a batch of streaming changes should trigger.</summary>
internal sealed record StreamingRouteResult(
    IReadOnlyList<NewMailSynchronizationRequested> MailSyncs,
    IReadOnlyList<NewCalendarSynchronizationRequested> CalendarSyncs)
{
    public static readonly StreamingRouteResult Empty =
        new(Array.Empty<NewMailSynchronizationRequested>(), Array.Empty<NewCalendarSynchronizationRequested>());

    public bool IsEmpty => MailSyncs.Count == 0 && CalendarSyncs.Count == 0;
}

/// <summary>
/// Translates a coalesced batch of push changes into the existing sync requests:
/// <list type="bullet">
/// <item>any folder-hierarchy event: one <c>FoldersOnly</c> mail sync (reconciles the tree);</item>
/// <item>mail item events: one <c>CustomFolders</c> sync over the affected folders only;</item>
/// <item>calendar item events: one <c>SingleCalendar</c> calendar sync over the affected calendars;</item>
/// <item>events in folders that are not synced locally (contacts, tasks, a brand-new folder): a
/// <c>FoldersOnly</c> sync so the tree picks the folder up.</item>
/// </list>
/// Pure aside from the folder/calendar lookups, so it is unit-testable with mocked services.
/// </summary>
internal sealed class StreamingEventRouter
{
    private readonly IFolderService _folderService;
    private readonly ICalendarService _calendarService;

    public StreamingEventRouter(IFolderService folderService, ICalendarService calendarService)
    {
        _folderService = folderService;
        _calendarService = calendarService;
    }

    public async Task<StreamingRouteResult> RouteAsync(Guid accountId, IReadOnlyCollection<StreamingChange> changes)
    {
        if (changes == null || changes.Count == 0)
            return StreamingRouteResult.Empty;

        var hierarchyChanged = changes.Any(c => c.IsFolderEvent);

        var itemFolderRemoteIds = changes
            .Where(c => !c.IsFolderEvent && !string.IsNullOrEmpty(c.RemoteFolderId))
            .Select(c => c.RemoteFolderId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // MAPI notifications name folders by MAPI id; resolve those to the same folder rows.
        var itemFolderMapiIds = changes
            .Where(c => !c.IsFolderEvent && string.IsNullOrEmpty(c.RemoteFolderId) && !string.IsNullOrEmpty(c.MapiFolderId))
            .Select(c => c.MapiFolderId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mailFolderIds = new List<Guid>();
        var calendarIds = new List<Guid>();
        var auxiliaryChanged = false; // contacts / tasks / not-yet-synced folder

        List<AccountCalendar> calendars = null;

        foreach (var remoteId in itemFolderRemoteIds)
        {
            var folder = await _folderService.GetFolderAsync(accountId, remoteId).ConfigureAwait(false);
            if (folder != null)
            {
                mailFolderIds.Add(folder.Id);
                continue;
            }

            calendars ??= await _calendarService.GetAccountCalendarsAsync(accountId).ConfigureAwait(false);
            var calendar = calendars?.FirstOrDefault(c => string.Equals(c.RemoteCalendarId, remoteId, StringComparison.Ordinal));
            if (calendar != null)
            {
                calendarIds.Add(calendar.Id);
                continue;
            }

            auxiliaryChanged = true;
        }

        foreach (var mapiId in itemFolderMapiIds)
        {
            var folder = await _folderService.GetFolderByMapiIdAsync(accountId, mapiId).ConfigureAwait(false);
            if (folder != null)
            {
                mailFolderIds.Add(folder.Id);
                continue;
            }

            // Calendars on the MAPI transport are keyed "mapi:" + folder id.
            calendars ??= await _calendarService.GetAccountCalendarsAsync(accountId).ConfigureAwait(false);
            var calendar = calendars?.FirstOrDefault(c => string.Equals(c.RemoteCalendarId, "mapi:" + mapiId, StringComparison.OrdinalIgnoreCase));
            if (calendar != null)
            {
                calendarIds.Add(calendar.Id);
                continue;
            }

            auxiliaryChanged = true;          // a folder not (yet) in the local tree: contacts/tasks or brand new
        }

        var mailSyncs = new List<NewMailSynchronizationRequested>();

        if (hierarchyChanged || (mailFolderIds.Count == 0 && auxiliaryChanged))
            mailSyncs.Add(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = accountId,
                Type = MailSynchronizationType.FoldersOnly
            }));

        if (mailFolderIds.Count > 0)
            mailSyncs.Add(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = accountId,
                Type = MailSynchronizationType.CustomFolders,
                SynchronizationFolderIds = mailFolderIds
            }));

        var calendarSyncs = calendarIds.Count == 0
            ? (IReadOnlyList<NewCalendarSynchronizationRequested>)Array.Empty<NewCalendarSynchronizationRequested>()
            : new List<NewCalendarSynchronizationRequested>
            {
                new(new CalendarSynchronizationOptions
                {
                    AccountId = accountId,
                    Type = CalendarSynchronizationType.SingleCalendar,
                    SynchronizationCalendarIds = calendarIds
                })
            };

        return new StreamingRouteResult(mailSyncs, calendarSyncs);
    }
}
