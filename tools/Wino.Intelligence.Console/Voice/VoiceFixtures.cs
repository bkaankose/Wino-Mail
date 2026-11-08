using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Intelligence.ConsoleApp.Voice;

/// <summary>
/// Events added to the data copy (never to the app's database) so event prompts have something to find, move,
/// cancel and answer. Times are relative to the run's clock.
/// </summary>
internal static class VoiceFixtures
{
    private sealed record Person(string Name, string Email);

    private static readonly Person Jay = new("Jay Kogan", "jay.kogan@gmail.com");
    private static readonly Person Christian = new("Christian Müller", "christian@agredoapplication.com");
    private static readonly Person Shiva = new("Shiva Brightfrost", "sb@adastria.ws");
    private static readonly Person Mirka = new("Mirosława Köse", "miroslawa.kose@gmail.com");
    private static readonly Person Ian = new("Ian Lawrence", "ianlawrence42@outlook.com");

    private sealed record Fixture(string Title, int Day, int Hour, int Minute, int Minutes, string? Location, Person? Organizer, Person[] Attendees, bool AllDay = false, CalendarItemShowAs ShowAs = CalendarItemShowAs.Busy);

    private static readonly Fixture[] Fixtures =
    [
        new("Team standup", 0, 9, 30, 15, null, null, [Jay, Christian]),
        new("Team standup", 1, 9, 30, 15, null, null, [Jay, Christian]),
        new("Team standup", 2, 9, 30, 15, null, null, [Jay, Christian]),
        new("Team standup", 3, 9, 30, 15, null, null, [Jay, Christian]),
        new("Team standup", 4, 9, 30, 15, null, null, [Jay, Christian]),
        new("Call with Mirka", 0, 19, 0, 30, null, null, [Mirka]),
        new("Budget review", 1, 10, 0, 60, "Room 4", null, [Jay]),
        new("Dentist appointment", 1, 15, 0, 60, "Smile Clinic", null, []),
        new("Product demo", 2, 14, 0, 60, "Teams", Jay, [Christian]),
        new("Yoga class", 2, 18, 0, 60, "Studio Flow", null, []),
        new("Lunch with Christian", 3, 12, 30, 60, "Cafe Nero", null, [Christian]),
        new("1:1 with Shiva", 4, 11, 0, 30, null, Shiva, []),
        new("Flight to Istanbul", 5, 8, 0, 210, "WAW Airport", null, []),
        new("Conference", 6, 0, 0, 0, "Berlin", null, [], AllDay: true, ShowAs: CalendarItemShowAs.OutOfOffice),
        new("Quarterly planning", 7, 13, 0, 120, "Room 1", null, [Jay, Christian, Ian]),
        new("Design review", 8, 16, 0, 45, null, Ian, []),
        new("Gym", -1, 18, 0, 60, null, null, []),
        new("Project kickoff", -2, 10, 0, 60, null, null, [Jay, Shiva]),
    ];

    public static async Task<int> SeedAsync(ICalendarService calendarService, IAccountService accountService, DateTime now)
    {
        var accounts = await accountService.GetAccountsAsync() ?? [];
        MailAccount? account = accounts.FirstOrDefault(static a => a.Name == "Work") ?? accounts.FirstOrDefault();
        if (account is null)
            return 0;

        var calendars = await calendarService.GetAccountCalendarsAsync(account.Id);
        var calendar = calendars.FirstOrDefault(static c => c.IsPrimary) ?? calendars.FirstOrDefault();
        if (calendar is null)
            return 0;

        var count = 0;
        foreach (var fixture in Fixtures)
        {
            var start = fixture.AllDay ? now.Date.AddDays(fixture.Day) : now.Date.AddDays(fixture.Day).AddHours(fixture.Hour).AddMinutes(fixture.Minute);
            var organizer = fixture.Organizer ?? new Person(account.SenderName ?? account.Name, account.Address);
            var item = new CalendarItem
            {
                Id = Guid.NewGuid(),
                RemoteEventId = $"voice-fixture-{count}",
                Title = fixture.Title,
                Location = fixture.Location,
                StartDate = start,
                StartTimeZone = TimeZoneInfo.Local.Id,
                EndTimeZone = TimeZoneInfo.Local.Id,
                DurationInSeconds = fixture.AllDay ? 86400 : fixture.Minutes * 60,
                OrganizerDisplayName = organizer.Name,
                OrganizerEmail = organizer.Email,
                CalendarId = calendar.Id,
                ShowAs = fixture.ShowAs,
                Status = CalendarItemStatus.Accepted,
            };

            var attendees = new List<CalendarEventAttendee>();
            if (fixture.Organizer is not null)
            {
                attendees.Add(new CalendarEventAttendee { Id = Guid.NewGuid(), CalendarItemId = item.Id, Name = fixture.Organizer.Name, Email = fixture.Organizer.Email, IsOrganizer = true, AttendenceStatus = AttendeeStatus.Accepted });
                attendees.Add(new CalendarEventAttendee { Id = Guid.NewGuid(), CalendarItemId = item.Id, Name = account.SenderName ?? account.Name, Email = account.Address, AttendenceStatus = AttendeeStatus.NeedsAction });
            }

            foreach (var person in fixture.Attendees)
                attendees.Add(new CalendarEventAttendee { Id = Guid.NewGuid(), CalendarItemId = item.Id, Name = person.Name, Email = person.Email, AttendenceStatus = AttendeeStatus.Accepted });

            await calendarService.CreateNewCalendarItemAsync(item, attendees);
            count++;
        }

        return count;
    }

    private sealed record TaskFixture(string Title, int? DueDay, bool Completed = false, bool Important = false);

    private static readonly TaskFixture[] Tasks =
    [
        new("Renew passport", 4),
        new("Buy milk", null, Completed: true),
        new("File the tax return", 10),
        new("Pay rent", -2),
        new("Send the invoice to Jay", -3, Important: true),
    ];

    /// <summary>
    /// Tasks and contacts for the task and contact prompts, in the data copy: tasks in the first writable list, and the
    /// fixture people as contacts in a local address book, with a "Family" list.
    /// </summary>
    public static async Task<int> SeedItemsAsync(ITaskService taskService, IContactService contactService, IAccountService accountService, DateTime now)
    {
        var count = 0;
        var list = (await taskService.GetTaskListsAsync() ?? []).FirstOrDefault(static list => !list.IsReadOnly);
        if (list is not null)
        {
            foreach (var fixture in Tasks)
            {
                await taskService.CreateTaskAsync(new AccountTask
                {
                    Id = Guid.NewGuid(),
                    MailAccountId = list.MailAccountId,
                    TaskListId = list.Id,
                    SourceKind = list.SourceKind,
                    Title = fixture.Title,
                    Notes = string.Empty,
                    DueDate = fixture.DueDay is int day ? now.Date.AddDays(day) : null,
                    IsCompleted = fixture.Completed,
                    CompletedAtUtc = fixture.Completed ? DateTime.UtcNow : null,
                    IsImportant = fixture.Important,
                });
                count++;
            }
        }

        var accounts = await accountService.GetAccountsAsync() ?? [];
        if ((accounts.FirstOrDefault(static a => a.Name == "Work") ?? accounts.FirstOrDefault()) is not { } account)
            return count;

        var book = await contactService.EnsureLocalAddressBookAsync(account.Id, "Voice fixtures");
        foreach (var person in new[] { Jay, Christian, Shiva })
        {
            var id = Guid.NewGuid();
            await contactService.StageCreateAsync(new AccountContact
            {
                Id = id,
                MailAccountId = account.Id,
                AddressBookId = book.Id,
                SourceKind = ContactSourceKind.Local,
                DisplayName = person.Name,
                EmailAddresses = [new ContactEmailAddress { Id = Guid.NewGuid(), ContactId = id, Address = person.Email, NormalizedAddress = ContactEmailAddress.Normalize(person.Email), IsPrimary = true }],
            });
            count++;
        }

        await contactService.CreateContactListAsync("Family");
        return count;
    }

    /// <summary>
    /// Stands in for the items a user would have selected: real items of the data copy that fit the described kind,
    /// count and the flags that pick items (unread, draft, invitation, organizer, overdue, completed, favorite).
    /// </summary>
    public static async Task<VoiceLocalSelection?> CreateSelectionAsync(IServiceProvider services, VoiceSelectionContext context, DateTime now)
    {
        var count = Math.Max(1, context.Count);
        var flags = context.Flags ?? [];
        var accounts = await services.GetRequiredService<IAccountService>().GetAccountsAsync() ?? [];
        var work = accounts.FirstOrDefault(static a => a.Name == "Work") ?? accounts.FirstOrDefault();

        switch (context.Kind)
        {
            case VoiceContextKind.Mail:
            {
                var folderService = services.GetRequiredService<IFolderService>();
                var mailService = services.GetRequiredService<IMailService>();
                var folderType = flags.Contains(VoiceContextFlags.Draft) ? SpecialFolderType.Draft : SpecialFolderType.Inbox;
                List<MailCopy> best = [];
                foreach (var account in accounts)
                {
                    if (await folderService.GetSpecialFolderByAccountIdAsync(account.Id, folderType) is not { } folder)
                        continue;

                    var mails = await mailService.GetMailsByFolderIdAsync(folder.Id) ?? [];
                    if (mails.Count <= best.Count)
                        continue;

                    foreach (var mail in mails)
                    {
                        mail.AssignedAccount = account;
                        mail.AssignedFolder = folder;
                    }

                    best = mails;
                }

                var ordered = best.OrderByDescending(static mail => mail.CreationDate);
                if (flags.Contains(VoiceContextFlags.Unread) && !flags.Contains(VoiceContextFlags.Read))
                    ordered = ordered.OrderBy(static mail => mail.IsRead).ThenByDescending(static mail => mail.CreationDate);
                return best.Count == 0 ? null : VoiceLocalSelection.ForMails(ordered.Take(count).ToList());
            }

            case VoiceContextKind.Event:
            {
                if (work is null)
                    return null;

                var calendarService = services.GetRequiredService<ICalendarService>();
                var calendars = await calendarService.GetAccountCalendarsAsync(work.Id) ?? [];
                var calendar = calendars.FirstOrDefault(static c => c.IsPrimary) ?? calendars.FirstOrDefault();
                if (calendar is null)
                    return null;

                var items = (await calendarService.GetCalendarEventsAsync(calendar, new Itenso.TimePeriod.TimeRange(now, now.AddDays(14))) ?? [])
                    .Where(static item => !item.IsAllDayEvent)
                    .OrderBy(static item => item.StartDate)
                    .ToList();
                bool IsMine(CalendarItem item) => string.IsNullOrWhiteSpace(item.OrganizerEmail) || string.Equals(item.OrganizerEmail, work.Address, StringComparison.OrdinalIgnoreCase);
                if (flags.Contains(VoiceContextFlags.Invitation))
                    items = items.Where(item => !IsMine(item)).ToList();
                else if (flags.Contains(VoiceContextFlags.Organizer))
                    items = items.Where(IsMine).ToList();

                foreach (var item in items)
                    item.AssignedCalendar ??= calendar;
                return items.Count == 0 ? null : VoiceLocalSelection.ForEvents(items.Take(count).ToList());
            }

            case VoiceContextKind.Task:
            {
                var tasks = await services.GetRequiredService<ITaskService>().GetTasksAsync() ?? [];
                var completed = flags.Contains(VoiceContextFlags.Completed);
                var picked = tasks.Where(task => task.IsCompleted == completed)
                    .OrderByDescending(task => flags.Contains(VoiceContextFlags.Overdue) && task.DueDate is { } due && due.Date < now.Date)
                    .ThenBy(static task => task.DueDate ?? DateTime.MaxValue)
                    .Take(count)
                    .ToList();
                return picked.Count == 0 ? null : VoiceLocalSelection.ForTasks(picked);
            }

            case VoiceContextKind.Contact:
            {
                var contactService = services.GetRequiredService<IContactService>();
                var picked = new List<AccountContact>();
                foreach (var person in new[] { Jay, Christian, Shiva })
                {
                    var page = await contactService.GetContactsPageAsync(new Wino.Core.Domain.Models.Contacts.ContactQueryFilter(SearchQuery: person.Email), 0, 1);
                    if (page?.Contacts.FirstOrDefault() is { } contact)
                        picked.Add(contact);
                }

                return picked.Count == 0 ? null : VoiceLocalSelection.ForContacts(picked.Take(count).ToList());
            }

            default:
            {
                if (work is null || await services.GetRequiredService<IFolderService>().GetSpecialFolderByAccountIdAsync(work.Id, SpecialFolderType.Inbox) is not { } inbox)
                    return null;

                return VoiceLocalSelection.ForFolder(inbox);
            }
        }
    }

    public static string DescribeSelection(VoiceLocalSelection selection) => selection.Kind switch
    {
        VoiceContextKind.Mail => string.Join("; ", selection.Mails.Take(3).Select(static mail => $"\"{Describer.Clip(mail.Subject, 40)}\" from {mail.FromAddress}")) + (selection.Mails.Count > 3 ? " ..." : string.Empty),
        VoiceContextKind.Event => string.Join("; ", selection.Events.Take(3).Select(Describer.Event)),
        VoiceContextKind.Task => string.Join("; ", selection.Tasks.Take(3).Select(static task => $"\"{task.Title}\"")),
        VoiceContextKind.Contact => string.Join("; ", selection.Contacts.Take(3).Select(static contact => contact.PrimaryEmailAddress ?? contact.DisplayName)),
        _ => selection.Folder?.FolderName ?? string.Empty
    };
}
