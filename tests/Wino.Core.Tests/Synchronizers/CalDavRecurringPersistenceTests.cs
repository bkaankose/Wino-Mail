using System.Reflection;
using FluentAssertions;
using Itenso.TimePeriod;
using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Integration.Processors;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

/// <summary>
/// Regression tests for recurring CalDAV series being collapsed into a single row (issue #1087).
/// </summary>
public class CalDavRecurringPersistenceTests : IAsyncLifetime
{
    private const string DailySeriesIcs = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Wino Mail//Tests//EN
        BEGIN:VEVENT
        UID:daily-series
        DTSTAMP:20260201T000000Z
        DTSTART:20260302T090000Z
        DTEND:20260302T093000Z
        RRULE:FREQ=DAILY;COUNT=5
        SUMMARY:Daily standup
        END:VEVENT
        END:VCALENDAR
        """;

    private InMemoryDatabaseService _databaseService = null!;
    private CalendarService _calendarService = null!;
    private ImapChangeProcessor _changeProcessor = null!;
    private AccountCalendar _calendar = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        _calendarService = new CalendarService(_databaseService);
        _changeProcessor = new ImapChangeProcessor(
            _databaseService,
            Mock.Of<IFolderService>(),
            Mock.Of<IMailService>(),
            Mock.Of<IAccountService>(),
            _calendarService,
            Mock.Of<IMimeFileService>(),
            Mock.Of<ICalendarIcsFileService>());

        _calendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Name = "Baikal",
            TimeZone = "UTC"
        };

        await _databaseService.Connection.InsertAsync(new MailAccount { Id = _calendar.AccountId, Name = "Baikal", Address = "user@example.com" });
        await _calendarService.InsertAccountCalendarAsync(_calendar);
    }

    public async Task DisposeAsync() => await _databaseService.DisposeAsync();

    [Fact]
    public async Task RecurringSeries_PersistsEveryOccurrenceAsOwnLinkedRow()
    {
        await PersistAsync(ParseEvents(DailySeriesIcs));

        var visibleEvents = await GetVisibleEventsAsync();

        visibleEvents.Should().HaveCount(5);
        visibleEvents.Select(e => e.StartDate.Day).Should().BeEquivalentTo([2, 3, 4, 5, 6]);

        var master = await _calendarService.GetCalendarItemAsync(_calendar.Id, "daily-series");
        master.Should().NotBeNull();
        master!.IsRecurringParent.Should().BeTrue();
        visibleEvents.Should().OnlyContain(e => e.RecurringCalendarItemId == master.Id);
    }

    [Fact]
    public async Task RecurringSeries_OccurrencesInsideVisibleRangeAreShownWhenSeriesStartedEarlier()
    {
        await PersistAsync(ParseEvents(DailySeriesIcs));

        var period = new TimeRange(new DateTime(2026, 3, 5), new DateTime(2026, 3, 7));
        var visibleEvents = await _calendarService.GetCalendarEventsAsync(_calendar, period);

        visibleEvents.Select(e => e.StartDate.Day).Should().BeEquivalentTo([5, 6]);
    }

    [Fact]
    public async Task RecurringSeries_ResyncIsIdempotent()
    {
        var events = ParseEvents(DailySeriesIcs);

        await PersistAsync(events);
        await PersistAsync(events);

        (await GetVisibleEventsAsync()).Should().HaveCount(5);
    }

    [Fact]
    public async Task RecurringSeries_RepairsRowCollapsedByPreviousLookup()
    {
        // Before the fix every occurrence resolved to the master row, which ended up
        // holding the first occurrence and pointing at itself as its own parent.
        var collapsedId = Guid.NewGuid();
        await _calendarService.CreateNewCalendarItemAsync(new CalendarItem
        {
            Id = collapsedId,
            CalendarId = _calendar.Id,
            RemoteEventId = "daily-series::20260302T090000Z",
            Title = "Daily standup",
            StartDate = new DateTime(2026, 3, 2, 9, 0, 0),
            DurationInSeconds = 1800,
            RecurringCalendarItemId = collapsedId
        }, null);

        await PersistAsync(ParseEvents(DailySeriesIcs));

        var master = await _calendarService.GetCalendarItemAsync(_calendar.Id, "daily-series");
        var visibleEvents = await GetVisibleEventsAsync();

        visibleEvents.Should().HaveCount(5);
        visibleEvents.Should().OnlyContain(e => e.RecurringCalendarItemId == master!.Id);
    }

    [Fact]
    public async Task GetCalendarItemAsync_MatchesClientTrackingSuffixButNotOccurrenceSuffix()
    {
        var trackedId = Guid.NewGuid();
        await _calendarService.CreateNewCalendarItemAsync(new CalendarItem
        {
            Id = Guid.NewGuid(),
            CalendarId = _calendar.Id,
            RemoteEventId = "provider-event".WithClientTrackingId(trackedId),
            StartDate = new DateTime(2026, 3, 1, 9, 0, 0),
            DurationInSeconds = 1800
        }, null);

        await _calendarService.CreateNewCalendarItemAsync(new CalendarItem
        {
            Id = Guid.NewGuid(),
            CalendarId = _calendar.Id,
            RemoteEventId = "caldav-series::20260302T090000Z",
            StartDate = new DateTime(2026, 3, 2, 9, 0, 0),
            DurationInSeconds = 1800
        }, null);

        (await _calendarService.GetCalendarItemAsync(_calendar.Id, "provider-event")).Should().NotBeNull();
        (await _calendarService.GetCalendarItemAsync(_calendar.Id, "caldav-series")).Should().BeNull();
        (await _calendarService.GetCalendarItemAsync(_calendar.Id, "caldav-series::20260303T090000Z")).Should().BeNull();
    }

    [Theory]
    [InlineData("uid::20260302T090000Z", "uid::20260302T090000Z")]
    [InlineData("uid", "uid")]
    [InlineData("AAMk=::0123456789abcdef0123456789abcdef", "AAMk=")]
    [InlineData("uid::20260302T090000Z::0123456789abcdef0123456789abcdef", "uid::20260302T090000Z")]
    public void StripClientTrackingSuffix_RemovesOnlyTrackingIds(string remoteEventId, string expected)
        => remoteEventId.StripClientTrackingSuffix().Should().Be(expected);

    private async Task PersistAsync(IEnumerable<CalDavCalendarEvent> events)
    {
        foreach (var remoteEvent in events)
        {
            await _changeProcessor.ManageCalendarEventAsync(remoteEvent, _calendar, organizerAccount: null);
        }
    }

    private Task<List<CalendarItem>> GetVisibleEventsAsync()
        => _calendarService.GetCalendarEventsAsync(_calendar, new TimeRange(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31)));

    private static List<CalDavCalendarEvent> ParseEvents(string icsContent)
    {
        var parseMethod = typeof(CalDavClient).GetMethod("ParseCalendarData", BindingFlags.NonPublic | BindingFlags.Static);

        var result = (List<CalDavCalendarEvent>)parseMethod!.Invoke(
            null,
            [
                icsContent,
                "https://calendar.example.com/event.ics",
                "\"etag\"",
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero)
            ])!;

        // Same ordering the CalDAV client applies: series masters before their occurrences.
        return result.OrderByDescending(e => e.IsSeriesMaster).ThenBy(e => e.Start).ToList();
    }
}
