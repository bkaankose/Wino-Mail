using System.Net;
using System.Net.Http;
using System.Xml.Linq;
using FluentAssertions;
using Itenso.TimePeriod;
using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.CardDav;
using Wino.Core.Integration.Processors;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class CalDavRecurrenceIntegrationTests
{
    [Theory]
    [InlineData("FREQ=DAILY", 7, false)]
    [InlineData("FREQ=DAILY", 7, true)]
    [InlineData("FREQ=WEEKLY;BYDAY=MO", 1, false)]
    [InlineData("FREQ=WEEKLY;BYDAY=MO", 1, true)]
    public async Task CalDavSync_SeriesStartingBeforeSyncWindow_DisplaysLaterOccurrences(
        string recurrenceRule, int expectedOccurrences, bool hasCorruptedCache)
    {
        var ics = $"""
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Wino Mail//Tests//EN
            BEGIN:VEVENT
            UID:old-series
            DTSTAMP:20200101T000000Z
            DTSTART:20200106T100000Z
            DTEND:20200106T110000Z
            RRULE:{recurrenceRule}
            SUMMARY:Recurring meeting
            END:VEVENT
            END:VCALENDAR
            """;
        var client = new CalDavClient(new CalendarReportTransport(ics));
        var remoteEvents = await client.GetCalendarEventsAsync(
            new CalDavConnectionSettings
            {
                ServiceUri = new Uri("https://dav.example.test/dav.php/"),
                Username = "user",
                Password = "test-password"
            },
            new CalDavCalendar { RemoteCalendarId = "https://dav.example.test/dav.php/calendars/user/default/" },
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

        remoteEvents.First().IsSeriesMaster.Should().BeTrue();
        remoteEvents.Should().Contain(value => value.IsRecurringInstance);

        await using var database = new InMemoryDatabaseService();
        await database.InitializeAsync();
        var calendarService = new CalendarService(database);
        var calendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Name = "Baikal calendar",
            TimeZone = "UTC"
        };
        await database.Connection.InsertAsync(new MailAccount { Id = calendar.AccountId, Address = "user@example.test" });
        await calendarService.InsertAccountCalendarAsync(calendar);

        if (hasCorruptedCache)
        {
            var survivingOccurrence = remoteEvents.Last();
            var corruptedId = Guid.NewGuid();
            await calendarService.CreateNewCalendarItemAsync(new CalendarItem
            {
                Id = corruptedId,
                CalendarId = calendar.Id,
                RemoteEventId = survivingOccurrence.RemoteEventId,
                RecurringCalendarItemId = corruptedId,
                StartDate = survivingOccurrence.Start.UtcDateTime,
                DurationInSeconds = 3600
            }, null);
        }

        var processor = new ImapChangeProcessor(
            database,
            Mock.Of<IFolderService>(),
            Mock.Of<IMailService>(),
            Mock.Of<IAccountService>(),
            calendarService,
            Mock.Of<IMimeFileService>(),
            Mock.Of<ICalendarIcsFileService>());

        foreach (var remoteEvent in remoteEvents)
            await processor.ManageCalendarEventAsync(remoteEvent, calendar, organizerAccount: null);

        var storedEvents = await database.Connection.Table<CalendarItem>().ToListAsync();
        storedEvents.Should().HaveCount(remoteEvents.Count, "each occurrence must keep its own remote identity");

        foreach (var remoteEvent in remoteEvents)
            await processor.ManageCalendarEventAsync(remoteEvent, calendar, organizerAccount: null);

        (await database.Connection.Table<CalendarItem>().CountAsync()).Should().Be(remoteEvents.Count);

        var periodStart = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc).ToLocalTime();
        var periodEnd = new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc).ToLocalTime();
        var visibleEvents = await calendarService.GetCalendarEventsAsync(calendar, new TimeRange(periodStart, periodEnd));

        visibleEvents.Should().HaveCount(expectedOccurrences);
        visibleEvents.Should().OnlyContain(value => value.IsRecurringChild && value.Title == "Recurring meeting");
        visibleEvents.Select(value => value.RemoteEventId).Should().OnlyHaveUniqueItems();
        var parent = await calendarService.GetCalendarItemAsync(calendar.Id, "old-series");
        parent.Should().NotBeNull();
        parent.StartDate.Should().Be(new DateTime(2020, 1, 6, 10, 0, 0));
        parent.Recurrence.Should().StartWith("RRULE:");
        visibleEvents.Should().OnlyContain(value => value.RecurringCalendarItemId == parent.Id);
        storedEvents.Where(value => value.IsRecurringChild).Should().OnlyContain(value => value.RecurringCalendarItemId == parent.Id);

        var removed = visibleEvents.First();
        await calendarService.DeleteCalendarItemAsync(removed.RemoteEventId, calendar.Id);
        (await database.Connection.FindAsync<CalendarItem>(removed.Id)).Should().BeNull();
        (await calendarService.GetCalendarItemAsync(calendar.Id, "old-series")).Should().NotBeNull();
        (await calendarService.GetCalendarEventsAsync(calendar, new TimeRange(periodStart, periodEnd)))
            .Should().HaveCount(expectedOccurrences - 1);
    }

    private sealed class CalendarReportTransport(string ics) : IDavTransport
    {
        public Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            DavAuthenticationProfile authentication,
            CancellationToken cancellationToken = default)
        {
            request.Method.Method.Should().Be("REPORT");
            XNamespace dav = "DAV:";
            XNamespace calDav = "urn:ietf:params:xml:ns:caldav";
            var response = new XElement(dav + "multistatus",
                new XElement(dav + "response",
                    new XElement(dav + "href", "/dav.php/calendars/user/default/series.ics"),
                    new XElement(dav + "propstat",
                        new XElement(dav + "prop",
                            new XElement(dav + "getetag", "\"series-etag\""),
                            new XElement(calDav + "calendar-data", ics)),
                        new XElement(dav + "status", "HTTP/1.1 200 OK"))));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                RequestMessage = request,
                Content = new StringContent(response.ToString())
            });
        }
    }
}
