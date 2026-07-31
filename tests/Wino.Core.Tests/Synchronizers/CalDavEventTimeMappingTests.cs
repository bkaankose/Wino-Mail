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
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero)
            ]);

        return result.Should().BeOfType<List<CalDavCalendarEvent>>().Subject;
    }
}
