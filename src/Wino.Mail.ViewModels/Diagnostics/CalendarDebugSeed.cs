#if DEBUG
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels.Diagnostics;

/// <summary>
/// Debug-only demo calendars for simulators and emulators, where no real calendar account can be added (the iOS bridge
/// command and the Android debug command activity <c>seedcalendar</c>): turns on the calendar for the first account (or
/// the one whose address contains the given text, such as the <see cref="MailDebugSeed"/> demo account), adds a writable
/// "Demo calendar" and a read-only "Holidays" calendar through the shared calendar service and fills the current week
/// with timed, overlapping, all-day and multi-day events. Without any account it creates a local IMAP account on a
/// closed loopback port with placeholder test credentials, so nothing leaves the device. The caller opens Calendar
/// afterwards; a calendar shell that already loaded its calendars shows them after the next mode activation.
/// </summary>
public static class CalendarDebugSeed
{
    public const string DemoCalendarRemoteId = "wino-demo-calendar";

    public static async Task<string> SeedAsync(IServiceProvider services, string? address = null)
    {
        var accounts = services.GetRequiredService<IAccountService>();
        var calendars = services.GetRequiredService<ICalendarService>();
        var all = await accounts.GetAccountsAsync();
        var account = all.FirstOrDefault(item => string.IsNullOrEmpty(address) || (item.Address?.Contains(address, StringComparison.OrdinalIgnoreCase) ?? false))
            ?? await CreateDemoAccountAsync(accounts);

        var existing = await calendars.GetAccountCalendarsAsync(account.Id);
        if (existing.Any(calendar => calendar.RemoteCalendarId == DemoCalendarRemoteId)) return "already seeded";

        account.IsCalendarAccessEnabled = true;
        account.IsCalendarAccessGranted = true;
        await accounts.UpdateAccountAsync(account);

        var demo = Calendar(account, DemoCalendarRemoteId, "Demo calendar", "#0078D4", primary: true, readOnly: false);
        var holidays = Calendar(account, "wino-demo-holidays", "Holidays", "#2E7D32", primary: false, readOnly: true);
        await calendars.InsertAccountCalendarAsync(demo);
        await calendars.InsertAccountCalendarAsync(holidays);

        var today = DateTime.Today;
        int created = 0;
        async Task Add(AccountCalendar calendar, string title, DateTime start, TimeSpan duration, string? location = null, bool attendees = false)
        {
            var item = new CalendarItem
            {
                Id = Guid.NewGuid(),
                RemoteEventId = "wino-demo-" + Guid.NewGuid().ToString("N"),
                Title = title,
                Description = $"<p>{title}: created by the calendar debug seed.</p>",
                Location = location ?? string.Empty,
                StartDate = start,
                DurationInSeconds = duration.TotalSeconds,
                StartTimeZone = TimeZoneInfo.Local.Id,
                EndTimeZone = TimeZoneInfo.Local.Id,
                OrganizerDisplayName = account.SenderName ?? account.Name,
                OrganizerEmail = account.Address,
                CalendarId = calendar.Id,
                Status = CalendarItemStatus.Accepted,
                ShowAs = CalendarItemShowAs.Busy,
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            };
            var people = new List<CalendarEventAttendee>();
            if (attendees)
            {
                people.Add(Attendee(item, "Ada Lovelace", "ada@example.com", AttendeeStatus.Accepted));
                people.Add(Attendee(item, "Grace Hopper", "grace@example.com", AttendeeStatus.Tentative));
                people.Add(Attendee(item, "Alan Turing", "alan@example.com", AttendeeStatus.NeedsAction));
            }
            await calendars.CreateNewCalendarItemAsync(item, people);
            created++;
        }

        await Add(demo, "Stand-up", today.AddHours(9), TimeSpan.FromMinutes(30), "Microsoft Teams");
        await Add(demo, "Design review: Calendar", today.AddHours(11), TimeSpan.FromMinutes(90), "Room 4.12", attendees: true);
        await Add(demo, "Lunch with Grace", today.AddHours(11.5), TimeSpan.FromMinutes(60), "Café Central");
        await Add(demo, "Focus time", today.AddHours(14), TimeSpan.FromHours(2));
        await Add(demo, "Release day", today, TimeSpan.FromDays(1));
        await Add(demo, "Planning offsite", today.AddDays(1), TimeSpan.FromDays(3), "Kraków");
        await Add(demo, "1:1 with Alan", today.AddDays(1).AddHours(10), TimeSpan.FromMinutes(45));
        await Add(demo, "Retrospective", today.AddDays(-1).AddHours(16), TimeSpan.FromHours(1), attendees: true);
        await Add(demo, "Dentist", today.AddDays(2).AddHours(8.5), TimeSpan.FromMinutes(40));
        await Add(demo, "Late deploy", today.AddDays(-2).AddHours(22), TimeSpan.FromHours(3));
        await Add(demo, "Sprint planning", today.AddDays(8).AddHours(14), TimeSpan.FromHours(2), "Room 4B", attendees: true);
        await Add(holidays, "Public holiday", today.AddDays(3), TimeSpan.FromDays(1));

        return $"seeded 2 calendars and {created} events on {account.Address}";
    }

    /// <summary>
    /// A local demo account when none exists: IMAP with placeholder test credentials for a closed loopback port, so
    /// synchronization fails fast and nothing leaves the device.
    /// </summary>
    private static async Task<MailAccount> CreateDemoAccountAsync(IAccountService accounts)
    {
        const string address = "calendar-demo@wino.test";
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Wino Calendar Demo",
            SenderName = "Wino Calendar Demo",
            Address = address,
            ProviderType = MailProviderType.IMAP4,
            AccountColorHex = "#0078D4",
            AttentionReason = AccountAttentionReason.None,
            IsMailAccessGranted = true,
            InitialSynchronizationRange = InitialSynchronizationRange.SixMonths
        };
        var server = new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            DisplayName = "Wino Calendar Demo",
            Address = address,
            IncomingServer = "127.0.0.1",
            IncomingServerPort = "1",
            IncomingServerUsername = address,
            IncomingServerPassword = "demo",
            OutgoingServer = "127.0.0.1",
            OutgoingServerPort = "1",
            OutgoingServerUsername = address,
            OutgoingServerPassword = "demo",
            IncomingServerType = CustomIncomingServerType.IMAP4,
            MaxConcurrentClients = 1
        };
        await accounts.CreateAccountAsync(account, server);
        return account;
    }

    private static AccountCalendar Calendar(MailAccount account, string remoteId, string name, string color, bool primary, bool readOnly) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = account.Id,
        RemoteCalendarId = remoteId,
        Name = name,
        IsPrimary = primary,
        IsReadOnly = readOnly,
        IsSynchronizationEnabled = true,
        IsExtended = true,
        BackgroundColorHex = color,
        TextColorHex = "#FFFFFF",
        TimeZone = TimeZoneInfo.Local.Id
    };

    private static CalendarEventAttendee Attendee(CalendarItem item, string name, string email, AttendeeStatus status) => new()
    {
        Id = Guid.NewGuid(),
        CalendarItemId = item.Id,
        Name = name,
        Email = email,
        AttendenceStatus = status
    };
}
#endif
