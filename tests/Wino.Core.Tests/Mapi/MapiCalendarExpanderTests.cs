using Wino.Core.Domain.Enums;
using Wino.Core.Synchronizers.Mapi;
using Wino.Mapi;
using Wino.Mapi.Calendar;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>Calendar over MAPI: series expansion over the sync window with deleted and modified instances.</summary>
public class MapiCalendarExpanderTests
{
    private static MapiAppointmentInfo Row(bool recurring, DateTime? start = null, DateTime? end = null, string tzKey = "Eastern Standard Time")
        => new(0x0001000000000ABC, "IPM.Appointment", "Standup", "body", "Room 1", start, end, false, 2, recurring, 0, 1, 1,
            "Matt", "matt@example.com", new DateTime(2026, 1, 1), new DateTime(2026, 1, 2), true, 10, null, tzKey);

    [Fact]
    public void SingleAppointment_InsideWindow_MapsOnce()
    {
        var start = new DateTime(2026, 9, 10, 13, 0, 0, DateTimeKind.Utc);
        var events = MapiCalendarExpander.Expand(Row(false, start, start.AddHours(1)), new DateTime(2026, 9, 1), new DateTime(2026, 10, 1));

        var single = events.Should().ContainSingle().Subject;
        single.RemoteId.Should().Be("mapi:0001000000000ABC");
        single.StartUtc.Should().Be(start);
        single.TimeZoneIana.Should().Be("America/New_York");
        single.ShowAs.Should().Be(CalendarItemShowAs.Busy);
        single.MyResponse.Should().Be(CalendarItemStatus.Accepted);
        single.ReminderMinutesBeforeStart.Should().Be(10);
        single.RecurringParentRemoteId.Should().BeNull();

        MapiCalendarExpander.Expand(Row(false, start, start.AddHours(1)), new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)).Should().BeEmpty();
    }

    [Fact]
    public void SingleAppointment_CarriesTheClientTrackingId()
    {
        var start = new DateTime(2026, 9, 10, 13, 0, 0, DateTimeKind.Utc);
        var tracking = Guid.NewGuid();
        var row = Row(false, start, start.AddHours(1)) with { ClientTrackingId = tracking.ToString("N") };

        var single = MapiCalendarExpander.Expand(row, new DateTime(2026, 9, 1), new DateTime(2026, 10, 1)).Should().ContainSingle().Subject;

        single.RemoteId.Should().Be($"mapi:0001000000000ABC::{tracking:N}");
    }

    [Fact]
    public void WeeklySeries_ExpandsWithinWindow_SkipsDeleted_AppliesException()
    {
        // Mondays and Wednesdays 09:00-10:00 Eastern from 7 Sep 2026, ten occurrences; 14 Sep deleted, 16 Sep moved to 11:00 as "Moved".
        var row = Row(true);
        row.Recurrence = new AppointmentRecurrence
        {
            RecurFrequency = AppointmentRecurrence.FrequencyWeekly,
            PatternType = AppointmentRecurrence.PatternWeek,
            Period = 1,
            DayOfWeekMask = 0x02 | 0x08,
            EndType = AppointmentRecurrence.EndAfterCount,
            OccurrenceCount = 10,
            FirstDayOfWeek = 1,
            StartDate = new DateTime(2026, 9, 7),
            StartTimeOffset = TimeSpan.FromHours(9),
            EndTimeOffset = TimeSpan.FromHours(10),
            DeletedInstanceDates = [new DateTime(2026, 9, 14), new DateTime(2026, 9, 16)],   // the modified date is listed here too, as the blob does
            ModifiedInstanceDates = [new DateTime(2026, 9, 16)],
            Exceptions = [new RecurrenceException(new DateTime(2026, 9, 16, 9, 0, 0), new DateTime(2026, 9, 16, 11, 0, 0), new DateTime(2026, 9, 16, 12, 0, 0), "Moved", null, null, null)],
        };

        var master = MapiCalendarExpander.Master(row);
        master.Should().NotBeNull();
        master!.RemoteId.Should().Be("mapi:0001000000000ABC");
        master.Recurrence.Should().StartWith("RRULE:FREQ=");
        master.RecurringParentRemoteId.Should().BeNull();
        MapiCalendarExpander.Master(Row(false, new DateTime(2026, 9, 7, 13, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 7, 14, 0, 0, DateTimeKind.Utc))).Should().BeNull("a single appointment has no master");

        var events = MapiCalendarExpander.Expand(row, new DateTime(2026, 9, 1), new DateTime(2026, 9, 30));
        events.Should().OnlyContain(e => e.RecurringParentRemoteId == "mapi:0001000000000ABC");

        // 7, 9, (14 deleted), 16, 21, 23, 28 September = 6 occurrences inside September.
        events.Should().HaveCount(6);
        events.Select(e => e.StartUtc.Date).Should().NotContain(new DateTime(2026, 9, 14));
        events[0].StartUtc.Should().Be(new DateTime(2026, 9, 7, 13, 0, 0, DateTimeKind.Utc));     // 09:00 EDT
        events[0].RemoteId.Should().Be("mapi:0001000000000ABC:20260907T130000Z");

        var moved = events.Single(e => e.Title == "Moved");
        moved.RemoteId.Should().Be("mapi:0001000000000ABC:20260916T130000Z");                     // keeps the original slot's id
        moved.StartUtc.Should().Be(new DateTime(2026, 9, 16, 15, 0, 0, DateTimeKind.Utc));       // 11:00 EDT
        moved.EndUtc.Should().Be(new DateTime(2026, 9, 16, 16, 0, 0, DateTimeKind.Utc));
        moved.Location.Should().Be("Room 1");
    }
}
