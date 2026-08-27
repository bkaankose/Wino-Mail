using FluentAssertions;
using Ical.Net;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Services;
using Xunit;
using IcalCalendar = Ical.Net.Calendar;

namespace Wino.Core.Tests.Synchronizers;

public sealed class CalDavIcsMutatorTests
{
    private const string RecurringIcs = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Wino Mail//Tests//EN
        X-WR-CALNAME:Preserved calendar
        BEGIN:VEVENT
        UID:series-1
        DTSTAMP:20260201T000000Z
        DTSTART:20260219T100000Z
        DTEND:20260219T110000Z
        RRULE:FREQ=DAILY;COUNT=3
        X-SERVER-PROPERTY:keep-me
        SUMMARY:Original
        END:VEVENT
        BEGIN:VEVENT
        UID:unrelated
        DTSTAMP:20260201T000000Z
        DTSTART:20260225T100000Z
        DTEND:20260225T110000Z
        SUMMARY:Unrelated
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void UpdateEvent_SingleOccurrence_PreservesSeriesAndOtherComponents()
    {
        var item = new CalendarItem
        {
            RemoteEventId = "series-1::20260220T100000Z",
            Title = "Moved occurrence",
            Description = "Changed",
            StartDate = new DateTime(2026, 2, 20, 12, 0, 0),
            DurationInSeconds = 7200,
            StartTimeZone = "UTC",
            EndTimeZone = "UTC",
            Status = CalendarItemStatus.Accepted,
            Visibility = CalendarItemVisibility.Private,
            ShowAs = CalendarItemShowAs.Busy
        };

        var result = CalDavIcsMutator.UpdateEvent(RecurringIcs, item, []);
        result.Should().Contain("DTEND:20260220T140000Z");
        var calendar = IcalCalendar.Load(result);

        calendar.Events.Should().HaveCount(3);
        var master = calendar.Events.Single(value => value.Uid == "series-1" && value.RecurrenceIdentifier == null);
        master.RecurrenceRule.Should().NotBeNull();
        master.Properties["X-SERVER-PROPERTY"].Value.Should().Be("keep-me");
        calendar.Events.Should().ContainSingle(value => value.Uid == "unrelated");

        var exception = calendar.Events.Single(value => value.Uid == "series-1" && value.RecurrenceIdentifier != null);
        exception.RecurrenceIdentifier!.StartTime.AsUtc.Should().Be(new DateTime(2026, 2, 20, 10, 0, 0));
        exception.Start.AsUtc.Should().Be(new DateTime(2026, 2, 20, 12, 0, 0));
        exception.End.AsUtc.Should().Be(new DateTime(2026, 2, 20, 14, 0, 0));
        exception.Summary.Should().Be("Moved occurrence");
        exception.RecurrenceRule.Should().BeNull();
    }

    [Fact]
    public void RemoveOccurrence_AddsExDateWithoutDeletingResourceSeries()
    {
        var result = CalDavIcsMutator.RemoveOccurrence(RecurringIcs, "series-1::20260220T100000Z");
        var calendar = IcalCalendar.Load(result);
        var master = calendar.Events.Single(value => value.Uid == "series-1");

        master.RecurrenceRule.Should().NotBeNull();
        master.ExceptionDates.GetAllDates()
            .Select(value => value.AsUtc)
            .Should().Contain(new DateTime(2026, 2, 20, 10, 0, 0));
        calendar.Events.Should().ContainSingle(value => value.Uid == "unrelated");
    }
}
