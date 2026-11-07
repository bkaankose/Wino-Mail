using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

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
}
