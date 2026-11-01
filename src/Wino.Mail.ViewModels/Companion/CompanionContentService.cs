#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Itenso.TimePeriod;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels.Companion;

/// <summary>
/// The "at a glance" content: unread mail in the folders the badge counts, upcoming events of the checked calendars,
/// My Day tasks and favorite contacts. The companion dashboard and the Windows widgets and Live Tile read it.
/// </summary>
public sealed class CompanionContentService(
    IAccountService accountService,
    IUnreadBadgeService unreadBadgeService,
    IFolderService folderService,
    IMailService mailService,
    ICalendarService calendarService,
    ITaskQueryService taskQueryService,
    IContactQueryService contactQueryService,
    IPreferencesService preferencesService)
{
    public Task<List<MailAccount>> GetAccountsAsync() => accountService.GetAccountsAsync();

    /// <summary>The folders whose unread mail the badge counts.</summary>
    public async Task<List<MailItemFolder>> GetCountedFoldersAsync(CancellationToken cancellationToken)
    {
        var snapshot = await unreadBadgeService.GetSnapshotAsync();
        var ids = snapshot.Accounts
            .SelectMany(static account => account.Contributions)
            .Select(static contribution => contribution.FolderId)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
            return [];

        return await folderService.GetFoldersByIdsAsync(ids, cancellationToken);
    }

    /// <summary>The newest unread messages, and how many unread messages there are in total.</summary>
    public async Task<(List<MailItemViewModel> Mail, int Count)> GetUnreadMailAsync(
        IReadOnlyList<MailItemFolder> folders,
        int take,
        DateTimeOffset? receivedAfterUtc,
        CancellationToken cancellationToken)
    {
        if (folders.Count == 0)
            return ([], 0);

        IReadOnlyList<IMailItemFolder> sources = folders.Cast<IMailItemFolder>().ToArray();
        var options = new MailListInitializationOptions(
            sources,
            FilterOptionType.Unread,
            SortingOptionType.ReceiveDate,
            CreateThreads: false,
            IsFocusedOnly: null,
            SearchQuery: string.Empty,
            DeduplicateByServerId: true,
            Take: take)
        {
            ExcludeDrafts = true,
            ReceivedAfterUtc = receivedAfterUtc
        };
        var mails = await mailService.FetchMailsAsync(options, cancellationToken);
        var count = await mailService.CountMailsAsync(options, cancellationToken);

        var viewModels = mails
            .Take(take)
            .Select(mail => new MailItemViewModel(mail, preferencesService.AccountNicknamePosition))
            .ToList();

        return (viewModels, count);
    }

    /// <summary>All My Day tasks: open ones first, then by importance, due date and last change.</summary>
    public async Task<List<TaskItemViewModel>> GetMyDayTasksAsync(CancellationToken cancellationToken)
    {
        var tasks = await taskQueryService.GetTasksAsync(view: TaskViewKind.MyDay, sort: TaskSortKind.Importance);
        var lists = await taskQueryService.GetTaskListsAsync();
        var names = lists.ToDictionary(static list => list.Id, static list => list.Title ?? string.Empty);
        cancellationToken.ThrowIfCancellationRequested();

        return tasks
            .OrderBy(static task => task.IsCompleted)
            .ThenByDescending(static task => task.IsImportant)
            .ThenBy(static task => task.DueDate ?? DateTime.MaxValue)
            .ThenByDescending(static task => task.ModifiedAtUtc)
            .Select(task => new TaskItemViewModel(
                task,
                names.TryGetValue(task.TaskListId, out var name) ? name : string.Empty)
            {
                ShowListName = true
            })
            .ToList();
    }

    /// <summary>
    /// Favorite contacts. With <paramref name="unreadFolders"/>, each carries its unread message count in those
    /// folders.
    /// </summary>
    public async Task<List<AccountContactViewModel>> GetFavoritesAsync(
        int take,
        IReadOnlyList<MailItemFolder>? unreadFolders,
        CancellationToken cancellationToken)
    {
        var query = await contactQueryService.GetContactsPageAsync(
            new ContactQueryFilter(FavoritesOnly: true),
            0,
            take,
            preferencesService.ContactSortOrder);
        var unread = unreadFolders is null
            ? new Dictionary<string, int>()
            : await mailService.GetUnreadSenderCountsAsync(
                unreadFolders.Select(static folder => folder.Id).ToArray(),
                query.Contacts
                    .Select(static contact => contact.PrimaryEmailAddress)
                    .Where(static address => !string.IsNullOrWhiteSpace(address))
                    .ToArray()!,
                cancellationToken);
        var accounts = await accountService.GetAccountsAsync();
        var accountNames = accounts.ToDictionary(static account => account.Id, static account => account.Name ?? string.Empty);

        return query.Contacts.Select(contact =>
        {
            var viewModel = new AccountContactViewModel(
                contact,
                accountNames.TryGetValue(contact.MailAccountId, out var name) ? name : string.Empty,
                displayFormat: preferencesService.ContactNameDisplayFormat,
                sortOrder: preferencesService.ContactSortOrder);
            var normalized = ContactEmailAddress.Normalize(contact.PrimaryEmailAddress);
            viewModel.UnreadCount = normalized is not null && unread.TryGetValue(normalized, out var count) ? count : 0;
            return viewModel;
        }).ToList();
    }

    /// <summary>
    /// Timed events from now until <paramref name="end"/> in the calendars checked in the calendar's account list
    /// (IsExtended), so this lists what the calendar shows, including secondary and subscribed calendars and their
    /// recurring events. Events the account declined are left out.
    /// </summary>
    public async Task<List<CalendarItemViewModel>> GetUpcomingEventsAsync(
        IReadOnlyList<MailAccount> accounts,
        DateTime end,
        int take,
        CancellationToken cancellationToken)
    {
        var now = DateTime.Now;
        var period = new TimeRange(now, end);
        var events = new List<CalendarItemViewModel>();

        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var calendars = await calendarService.GetAccountCalendarsAsync(account.Id);
            foreach (var calendar in calendars.Where(static calendar => calendar.IsExtended))
            {
                var items = await calendarService.GetCalendarEventsAsync(calendar, period);
                foreach (var item in items.Where(item =>
                             !item.IsAllDayEvent &&
                             item.LocalEndDate > now &&
                             item.LocalStartDate < end))
                {
                    var attendees = await calendarService.GetAttendeesAsync(item.EventTrackingId);
                    var isDeclined = attendees.Any(attendee =>
                        attendee.AttendenceStatus == AttendeeStatus.Declined &&
                        string.Equals(attendee.Email?.Trim(), account.Address?.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (isDeclined)
                        continue;

                    var viewModel = new CalendarItemViewModel(item) { DisplayingPeriod = period };
                    foreach (var attendee in attendees)
                        viewModel.Attendees.Add(attendee);
                    events.Add(viewModel);
                }
            }
        }

        return events
            .OrderBy(static item => item.StartDate)
            .ThenBy(static item => item.Id)
            .Take(take)
            .ToList();
    }
}
