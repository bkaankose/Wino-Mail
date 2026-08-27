using System.Reflection;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Integration.Processors;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public class CalDavEventTimeMappingTests
{
    [Fact]
    public void ParseCalendarData_UtcEvent_AssignsUtcTimeZone()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Wino Mail//Tests//EN
            CALSCALE:GREGORIAN
            BEGIN:VEVENT
            UID:utc-event
            DTSTAMP:20260201T000000Z
            DTSTART:20260219T010000Z
            DTEND:20260219T020000Z
            SUMMARY:UTC Event
            END:VEVENT
            END:VCALENDAR
            """;

        var events = ParseEvents(ics);

        events.Should().ContainSingle();
        events[0].StartTimeZone.Should().Be(TimeZoneInfo.Utc.Id);
        events[0].EndTimeZone.Should().Be(TimeZoneInfo.Utc.Id);
    }

    [Fact]
    public void ParseCalendarData_UnboundedRecurrence_StopsAtWindowEnd()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Apple Inc.//iCloud Calendar//EN
            CALSCALE:GREGORIAN
            BEGIN:VEVENT
            UID:icloud-unbounded-event
            DTSTAMP:20260201T000000Z
            DTSTART:20260219T010000Z
            DTEND:20260219T020000Z
            RRULE:FREQ=DAILY
            SUMMARY:Unbounded iCloud Event
            END:VEVENT
            END:VCALENDAR
            """;

        var events = ParseEvents(
            ics,
            new DateTimeOffset(2026, 2, 19, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 2, 22, 0, 0, 0, TimeSpan.Zero));

        events.Should().HaveCount(4);
        events.Should().ContainSingle(calendarEvent => calendarEvent.IsSeriesMaster);
        events.Count(calendarEvent => calendarEvent.IsRecurringInstance).Should().Be(3);
        events.Where(calendarEvent => calendarEvent.IsRecurringInstance)
            .Should().OnlyContain(calendarEvent => calendarEvent.Start < new DateTimeOffset(2026, 2, 22, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ParseCalendarData_RecurringDuration_UsesEffectiveOccurrenceEnd()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Wino Mail//Tests//EN
            BEGIN:VEVENT
            UID:duration-series
            DTSTAMP:20260201T000000Z
            DTSTART:20260219T100000Z
            DURATION:P4D
            RRULE:FREQ=DAILY;COUNT=1
            SUMMARY:Long event
            END:VEVENT
            END:VCALENDAR
            """;

        var occurrence = ParseEvents(ics).Single(value => value.IsRecurringInstance);

        (occurrence.End - occurrence.Start).Should().Be(TimeSpan.FromDays(4));
    }

    [Theory]
    [InlineData("DTSTART:20260219T100000Z", 0)]
    [InlineData("DTSTART;VALUE=DATE:20260219", 86400)]
    public void ParseCalendarData_MissingEnd_UsesRfcDefaultDuration(string startLine, int expectedSeconds)
    {
        var ics = $"""
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Wino Mail//Tests//EN
            BEGIN:VEVENT
            UID:no-end
            DTSTAMP:20260201T000000Z
            {startLine}
            SUMMARY:No end
            END:VEVENT
            END:VCALENDAR
            """;

        var calendarEvent = ParseEvents(ics).Single();

        (calendarEvent.End - calendarEvent.Start).TotalSeconds.Should().Be(expectedSeconds);
    }

    [Fact]
    public void ParseCalendarData_FloatingMultiDayEvent_PreservesNominalDurationAndFloatingSemantics()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Wino Mail//Tests//EN
            BEGIN:VEVENT
            UID:floating-event
            DTSTAMP:20260201T000000Z
            DTSTART:20260328T120000
            DTEND:20260330T120000
            SUMMARY:Floating event
            END:VEVENT
            END:VCALENDAR
            """;

        var calendarEvent = ParseEvents(ics).Single();

        calendarEvent.StartIsFloating.Should().BeTrue();
        calendarEvent.EndIsFloating.Should().BeTrue();
        calendarEvent.StartTimeZone.Should().BeEmpty();
        (calendarEvent.End - calendarEvent.Start).Should().Be(TimeSpan.FromDays(2));
    }

    [Fact]
    public void ParseCalendarData_MovedException_UsesExceptionTimes()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Wino Mail//Tests//EN
            BEGIN:VEVENT
            UID:moved-series
            DTSTAMP:20260201T000000Z
            DTSTART:20260219T100000Z
            DTEND:20260219T110000Z
            RRULE:FREQ=DAILY;COUNT=3
            SUMMARY:Original
            END:VEVENT
            BEGIN:VEVENT
            UID:moved-series
            RECURRENCE-ID:20260220T100000Z
            DTSTAMP:20260201T000000Z
            DTSTART:20260220T120000Z
            DTEND:20260220T140000Z
            SUMMARY:Moved
            END:VEVENT
            END:VCALENDAR
            """;

        var moved = ParseEvents(ics).Single(value => value.RemoteEventId == "moved-series::20260220T100000Z");

        moved.Start.Should().Be(new DateTimeOffset(2026, 2, 20, 12, 0, 0, TimeSpan.Zero));
        moved.End.Should().Be(new DateTimeOffset(2026, 2, 20, 14, 0, 0, TimeSpan.Zero));
        moved.Title.Should().Be("Moved");
    }

    [Fact]
    public void ParseCalendarData_ThisAndFuture_ShiftsFollowingOccurrences()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Wino Mail//Tests//EN
            BEGIN:VEVENT
            UID:range-series
            DTSTAMP:20260201T000000Z
            DTSTART:20260219T100000Z
            DTEND:20260219T110000Z
            RRULE:FREQ=DAILY;COUNT=3
            SUMMARY:Original
            END:VEVENT
            BEGIN:VEVENT
            UID:range-series
            RECURRENCE-ID;RANGE=THISANDFUTURE:20260220T100000Z
            DTSTAMP:20260201T000000Z
            DTSTART:20260220T110000Z
            DTEND:20260220T130000Z
            SUMMARY:Shifted
            END:VEVENT
            END:VCALENDAR
            """;

        var third = ParseEvents(ics).Single(value => value.RemoteEventId == "range-series::20260221T100000Z");

        third.Start.Should().Be(new DateTimeOffset(2026, 2, 21, 11, 0, 0, TimeSpan.Zero));
        third.End.Should().Be(new DateTimeOffset(2026, 2, 21, 13, 0, 0, TimeSpan.Zero));
        third.Title.Should().Be("Shifted");
    }

    [Fact]
    public async Task ManageCalendarEventAsync_PersistsWallClockTimeForSourceTimeZone()
    {
        var calendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            Name = "Calendar"
        };

        var remoteEvent = new CalDavCalendarEvent
        {
            RemoteEventId = "event-1",
            Title = "Wall Clock Event",
            Start = new DateTimeOffset(2026, 2, 19, 1, 0, 0, TimeSpan.FromHours(1)),
            End = new DateTimeOffset(2026, 2, 19, 2, 0, 0, TimeSpan.FromHours(1)),
            StartTimeZone = "Europe/Berlin",
            EndTimeZone = "Europe/Berlin"
        };

        CalendarItem? capturedItem = null;
        var calendarService = new Mock<ICalendarService>();
        calendarService
            .Setup(x => x.GetCalendarItemAsync(calendar.Id, remoteEvent.RemoteEventId))
            .ReturnsAsync((CalendarItem?)null);
        calendarService
            .Setup(x => x.CreateNewCalendarItemAsync(It.IsAny<CalendarItem>(), It.IsAny<List<CalendarEventAttendee>>()))
            .Callback<CalendarItem, List<CalendarEventAttendee>>((item, _) => capturedItem = item)
            .Returns(Task.CompletedTask);
        calendarService
            .Setup(x => x.SaveRemindersAsync(It.IsAny<Guid>(), It.IsAny<List<Reminder>>()))
            .Returns(Task.CompletedTask);

        var sut = new ImapChangeProcessor(
            Mock.Of<IDatabaseService>(),
            Mock.Of<IFolderService>(),
            Mock.Of<IMailService>(),
            Mock.Of<IAccountService>(),
            calendarService.Object,
            Mock.Of<IMimeFileService>(),
            Mock.Of<ICalendarIcsFileService>());

        await sut.ManageCalendarEventAsync(remoteEvent, calendar, organizerAccount: null);

        capturedItem.Should().NotBeNull();
        var savedItem = capturedItem!;
        savedItem.StartDate.Should().Be(new DateTime(2026, 2, 19, 1, 0, 0));
        savedItem.DurationInSeconds.Should().Be(3600);
        savedItem.StartTimeZone.Should().Be("Europe/Berlin");
        savedItem.EndTimeZone.Should().Be("Europe/Berlin");
    }

    private static List<CalDavCalendarEvent> ParseEvents(string icsContent)
        => ParseEvents(
            icsContent,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero));

    private static List<CalDavCalendarEvent> ParseEvents(
        string icsContent,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc)
    {
        var parseMethod = typeof(CalDavClient).GetMethod(
            "ParseCalendarData",
            BindingFlags.NonPublic | BindingFlags.Static);

        parseMethod.Should().NotBeNull();

        var result = parseMethod!.Invoke(
            null,
            [
                icsContent,
                "https://calendar.example.com/event.ics",
                "\"etag\"",
                windowStartUtc,
                windowEndUtc
            ]);

        return result.Should().BeOfType<List<CalDavCalendarEvent>>().Subject;
    }
}
