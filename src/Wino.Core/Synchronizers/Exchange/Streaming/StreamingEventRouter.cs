using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
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
    IReadOnlyList<NewCalendarSynchronizationRequested> CalendarSyncs,
    IReadOnlyList<NewContactSynchronizationRequested> ContactSyncs,
    IReadOnlyList<NewTaskSynchronizationRequested> TaskSyncs)
{
    public static readonly StreamingRouteResult Empty = new(
        Array.Empty<NewMailSynchronizationRequested>(),
        Array.Empty<NewCalendarSynchronizationRequested>(),
        Array.Empty<NewContactSynchronizationRequested>(),
        Array.Empty<NewTaskSynchronizationRequested>());

    public bool IsEmpty => MailSyncs.Count == 0 && CalendarSyncs.Count == 0 && ContactSyncs.Count == 0 && TaskSyncs.Count == 0;
}

/// <summary>
/// Translates a coalesced batch of push changes into the existing sync requests:
/// <list type="bullet">
/// <item>any folder-hierarchy event: one <c>FoldersOnly</c> mail sync (reconciles the tree);</item>
/// <item>mail item events: one <c>CustomFolders</c> sync over the affected folders only;</item>
/// <item>calendar item events: one <c>SingleCalendar</c> calendar sync over the affected calendars;</item>
/// <item>contacts and tasks folder events: one <c>Full</c> contact sync or task sync;</item>
/// <item>events in folders that are not synced locally (a brand-new folder, or contacts and tasks
/// while those run locally): a <c>FoldersOnly</c> sync so the tree picks the folder up.</item>
/// </list>
/// Pure aside from the folder, calendar, address-book and task-list lookups, so it is unit-testable
/// with mocked services.
/// </summary>
internal sealed class StreamingEventRouter
{
    private const string MapiIdPrefix = "mapi:";

    private readonly IFolderService _folderService;
    private readonly ICalendarService _calendarService;
    private readonly IContactService _contactService;
    private readonly ITaskService _taskService;

    public StreamingEventRouter(IFolderService folderService, ICalendarService calendarService, IContactService contactService = null, ITaskService taskService = null)
    {
        _folderService = folderService;
        _calendarService = calendarService;
        _contactService = contactService;
        _taskService = taskService;
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
        var contactsChanged = false;
        var tasksChanged = false;
        var auxiliaryChanged = false; // a folder not (yet) in the local tree

        List<AccountCalendar> calendars = null;
        List<ContactAddressBook> addressBooks = null;
        List<AccountTaskList> taskLists = null;

        async Task ClassifyAsync(string remoteKey, StringComparison comparison)
        {
            calendars ??= await _calendarService.GetAccountCalendarsAsync(accountId).ConfigureAwait(false);
            if (calendars?.Any(c => string.Equals(c.RemoteCalendarId, remoteKey, comparison)) == true)
            {
                calendarIds.AddRange(calendars.Where(c => string.Equals(c.RemoteCalendarId, remoteKey, comparison)).Select(c => c.Id));
                return;
            }

            if (_contactService != null)
            {
                addressBooks ??= await _contactService.GetAddressBooksAsync(accountId).ConfigureAwait(false);
                if (addressBooks?.Any(b => b.SourceKind == ContactSourceKind.Exchange && string.Equals(b.RemoteId, remoteKey, comparison)) == true)
                {
                    contactsChanged = true;
                    return;
                }
            }

            if (_taskService != null)
            {
                taskLists ??= await _taskService.GetTaskListsAsync(accountId).ConfigureAwait(false);
                if (taskLists?.Any(l => l.SourceKind == TaskSourceKind.Exchange && string.Equals(l.RemoteId, remoteKey, comparison)) == true)
                {
                    tasksChanged = true;
                    return;
                }
            }

            auxiliaryChanged = true;
        }

        foreach (var remoteId in itemFolderRemoteIds)
        {
            var folder = await _folderService.GetFolderAsync(accountId, remoteId).ConfigureAwait(false);
            if (folder != null)
            {
                mailFolderIds.Add(folder.Id);
                continue;
            }

            await ClassifyAsync(remoteId, StringComparison.Ordinal).ConfigureAwait(false);
        }

        foreach (var mapiId in itemFolderMapiIds)
        {
            var folder = await _folderService.GetFolderByMapiIdAsync(accountId, mapiId).ConfigureAwait(false);
            if (folder != null)
            {
                mailFolderIds.Add(folder.Id);
                continue;
            }

            // Calendars, address books and task lists on the MAPI transport are keyed "mapi:" + folder id.
            await ClassifyAsync(MapiIdPrefix + mapiId, StringComparison.OrdinalIgnoreCase).ConfigureAwait(false);
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
                    SynchronizationCalendarIds = calendarIds.Distinct().ToList()
                })
            };

        var contactSyncs = !contactsChanged
            ? (IReadOnlyList<NewContactSynchronizationRequested>)Array.Empty<NewContactSynchronizationRequested>()
            : new List<NewContactSynchronizationRequested>
            {
                new(new ContactSynchronizationOptions { AccountId = accountId, Type = ContactSynchronizationType.Full })
            };

        var taskSyncs = !tasksChanged
            ? (IReadOnlyList<NewTaskSynchronizationRequested>)Array.Empty<NewTaskSynchronizationRequested>()
            : new List<NewTaskSynchronizationRequested>
            {
                new(new TaskSynchronizationOptions { AccountId = accountId, Type = TaskSynchronizationType.Full })
            };

        return new StreamingRouteResult(mailSyncs, calendarSyncs, contactSyncs, taskSyncs);
    }
}
