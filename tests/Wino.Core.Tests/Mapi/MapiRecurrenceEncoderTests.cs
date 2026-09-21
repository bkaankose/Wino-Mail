using Wino.Mapi;
using Wino.Mapi.Calendar;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>The write side of PidLidAppointmentRecur: RRULE to pattern, pattern to blob, and the blob read back by the decoder.</summary>
public class MapiRecurrenceEncoderTests
{
    private static readonly DateTime Start = new(2026, 9, 15, 9, 0, 0);       // a Tuesday, wall clock
    private static readonly TimeSpan HalfHour = TimeSpan.FromMinutes(30);

    private static AppointmentRecurrence RoundTrip(AppointmentRecurrence r)
        => AppointmentRecurrence.Parse(RecurrenceEncoder.Encode(r));

    [Fact]
    public void Daily_EveryTwoDays_UntilDate()
    {
        var r = RecurrenceEncoder.FromRRule("RRULE:FREQ=DAILY;INTERVAL=2;UNTIL=20260925T235959", Start, HalfHour);

        r.RecurFrequency.Should().Be(AppointmentRecurrence.FrequencyDaily);
        r.PatternType.Should().Be(AppointmentRecurrence.PatternDay);
        r.Period.Should().Be(2880, "minutes");
        r.EndType.Should().Be(AppointmentRecurrence.EndAfterDate);
        r.StartDate.Should().Be(Start.Date);
        r.EndDate.Should().Be(new DateTime(2026, 9, 25), "the last occurrence on or before UNTIL");
        r.OccurrenceCount.Should().Be(6, "15, 17, 19, 21, 23, 25");
        r.StartTimeOffset.Should().Be(TimeSpan.FromHours(9));
        r.EndTimeOffset.Should().Be(TimeSpan.FromMinutes(570));

        var back = RoundTrip(r);
        back.ToRRule().Should().StartWith("FREQ=DAILY;INTERVAL=2");
        back.ToRRule().Should().Contain("UNTIL=20260925T090000");
        back.FirstStart.Should().Be(Start);
        RecurrenceEncoder.FirstDateTime(r).Should().Be(AppointmentRecurrence.ToMinutes(Start.Date) % 2880);
    }

    [Fact]
    public void Weekly_MondayWednesday_StartMovesToFirstMatch_Count()
    {
        var r = RecurrenceEncoder.FromRRule("FREQ=WEEKLY;INTERVAL=1;BYDAY=MO,WE;COUNT=4", Start, HalfHour);

        r.RecurFrequency.Should().Be(AppointmentRecurrence.FrequencyWeekly);
        r.PatternType.Should().Be(AppointmentRecurrence.PatternWeek);
        r.DayOfWeekMask.Should().Be(0x02 | 0x08);
        r.StartDate.Should().Be(new DateTime(2026, 9, 16), "the Tuesday start moves to Wednesday");
        r.EndType.Should().Be(AppointmentRecurrence.EndAfterCount);
        r.OccurrenceCount.Should().Be(4);
        r.EndDate.Should().Be(new DateTime(2026, 9, 28), "16 We, 21 Mo, 23 We, 28 Mo");

        var back = RoundTrip(r);
        back.ToRRule().Should().Be("FREQ=WEEKLY;INTERVAL=1;BYDAY=MO,WE;COUNT=4;WKST=SU");

        // FirstDateTime is the Sunday that starts the week of the first occurrence.
        RecurrenceEncoder.FirstDateTime(r).Should().Be(AppointmentRecurrence.ToMinutes(new DateTime(2026, 9, 13)) % (7 * 1440));
    }

    [Fact]
    public void EveryWeekday_IsWeeklyShapeUnderDailyFrequency()
    {
        var r = RecurrenceEncoder.FromRRule("FREQ=DAILY;INTERVAL=1;BYDAY=MO,TU,WE,TH,FR", Start, HalfHour);
        r.RecurFrequency.Should().Be(AppointmentRecurrence.FrequencyDaily);
        r.PatternType.Should().Be(AppointmentRecurrence.PatternWeek);
        r.DayOfWeekMask.Should().Be(0x3E);
        r.Period.Should().Be(1);
        r.EndType.Should().Be(AppointmentRecurrence.EndNever);
        r.OccurrenceCount.Should().Be(RecurrenceEncoder.NoEndOccurrenceCount);
        RoundTrip(r).ToRRule().Should().StartWith("FREQ=WEEKLY;INTERVAL=1;BYDAY=MO,TU,WE,TH,FR");
        RecurrenceEncoder.Occurrences(r, 6).Select(d => d.DayOfWeek).Should().NotContain(DayOfWeek.Saturday);

        var blob = RecurrenceEncoder.Encode(r);
        BitConverter.ToUInt32(blob, blob.Length - 30).Should().Be(RecurrenceEncoder.NoEndDateMinutes, "EndDate pinned for a series with no end");
    }

    [Fact]
    public void Monthly_ByDay_AndNthWeekday_AndYearly()
    {
        var byDay = RecurrenceEncoder.FromRRule("FREQ=MONTHLY;INTERVAL=1", Start, HalfHour);
        byDay.PatternType.Should().Be(AppointmentRecurrence.PatternMonth);
        byDay.Day.Should().Be(15);
        RecurrenceEncoder.Occurrences(byDay, 3).Should().Equal(new DateTime(2026, 9, 15), new DateTime(2026, 10, 15), new DateTime(2026, 11, 15));
        RoundTrip(byDay).ToRRule().Should().StartWith("FREQ=MONTHLY;INTERVAL=1;BYMONTHDAY=15");

        var lastDay = RecurrenceEncoder.FromRRule("FREQ=MONTHLY;BYMONTHDAY=-1", new DateTime(2026, 1, 31, 9, 0, 0), HalfHour);
        lastDay.Day.Should().Be(31);
        RecurrenceEncoder.Occurrences(lastDay, 3).Should().Equal(new DateTime(2026, 1, 31), new DateTime(2026, 2, 28), new DateTime(2026, 3, 31));

        var nth = RecurrenceEncoder.FromRRule("FREQ=MONTHLY;INTERVAL=2;BYDAY=3TU", Start, HalfHour);
        nth.PatternType.Should().Be(AppointmentRecurrence.PatternMonthNth);
        nth.DayOfWeekMask.Should().Be(0x04);
        nth.Nth.Should().Be(3);
        nth.StartDate.Should().Be(new DateTime(2026, 9, 15), "15 Sep 2026 is the third Tuesday");
        RecurrenceEncoder.Occurrences(nth, 2).Should().Equal(new DateTime(2026, 9, 15), new DateTime(2026, 11, 17));
        RoundTrip(nth).ToRRule().Should().StartWith("FREQ=MONTHLY;INTERVAL=2;BYDAY=TU;BYSETPOS=3");

        var last = RecurrenceEncoder.FromRRule("FREQ=MONTHLY;BYDAY=FR;BYSETPOS=-1", Start, HalfHour);
        last.Nth.Should().Be(5);
        RecurrenceEncoder.Occurrences(last, 2).Should().Equal(new DateTime(2026, 9, 25), new DateTime(2026, 10, 30));

        var yearly = RecurrenceEncoder.FromRRule("FREQ=YEARLY;INTERVAL=1", Start, HalfHour);
        yearly.RecurFrequency.Should().Be(AppointmentRecurrence.FrequencyYearly);
        yearly.Period.Should().Be(12);
        yearly.Day.Should().Be(15);
        RecurrenceEncoder.Occurrences(yearly, 2).Should().Equal(new DateTime(2026, 9, 15), new DateTime(2027, 9, 15));
        RoundTrip(yearly).ToRRule().Should().StartWith("FREQ=YEARLY;INTERVAL=1;BYMONTHDAY=15;BYMONTH=9");
    }

    [Fact]
    public void Blob_HasTheSpecLayout()
    {
        var r = RecurrenceEncoder.FromRRule("FREQ=WEEKLY;BYDAY=TU;COUNT=2", Start, HalfHour);
        var blob = RecurrenceEncoder.Encode(r);

        BitConverter.ToUInt16(blob, 0).Should().Be(0x3004);
        BitConverter.ToUInt16(blob, 2).Should().Be(0x3004);
        BitConverter.ToUInt16(blob, 4).Should().Be(AppointmentRecurrence.FrequencyWeekly);
        BitConverter.ToUInt16(blob, 6).Should().Be(AppointmentRecurrence.PatternWeek);
        BitConverter.ToUInt16(blob, 8).Should().Be(0, "CalendarType");
        BitConverter.ToUInt32(blob, 14).Should().Be(1, "Period");
        BitConverter.ToUInt32(blob, 18).Should().Be(0, "SlidingFlag");
        BitConverter.ToUInt32(blob, 22).Should().Be(0x04, "DayOfWeekMask");
        BitConverter.ToUInt32(blob, 26).Should().Be(AppointmentRecurrence.EndAfterCount);
        BitConverter.ToUInt32(blob, 30).Should().Be(2);
        BitConverter.ToUInt32(blob, 34).Should().Be(0, "FirstDOW Sunday");
        BitConverter.ToUInt32(blob, 38).Should().Be(0, "no deleted");
        BitConverter.ToUInt32(blob, 42).Should().Be(0, "no modified");
        BitConverter.ToUInt32(blob, 46).Should().Be(AppointmentRecurrence.ToMinutes(Start.Date));
        BitConverter.ToUInt32(blob, 50).Should().Be(AppointmentRecurrence.ToMinutes(new DateTime(2026, 9, 22)), "EndDate = last occurrence for a counted series");
        BitConverter.ToUInt32(blob, 54).Should().Be(0x3006);
        BitConverter.ToUInt32(blob, 58).Should().Be(0x3009);
        BitConverter.ToUInt32(blob, 62).Should().Be(540, "StartTimeOffset");
        BitConverter.ToUInt32(blob, 66).Should().Be(570, "EndTimeOffset");
        BitConverter.ToUInt16(blob, 70).Should().Be(0, "ExceptionCount");
        blob.Length.Should().Be(72 + 4 + 4, "ReservedBlock1Size + ReservedBlock2Size");
    }

    [Fact]
    public void AppointmentProperties_CarryTheSeries()
    {
        var tags = new MapiCalendarTags(
            0x820D0040, 0x820E0040, 0x8208001F, 0x8215000B, 0x82050003, 0x8223000B, 0x82160102,
            0x82180003, 0x82170003, 0x825E0102,
            0x8503000B, 0x85010003, 0x80030102,
            0x80230102, 0x801A0040, 0x80010040, 0x8002001F,
            0x82010003, 0x82200040, 0x82240003, 0x8230001F,
            0, RecurrenceType: 0x82310003, ClipStart: 0x82350040, ClipEnd: 0x82360040);
        var write = new MapiCalendarOperations.AppointmentWrite
        {
            StartUtc = new DateTime(2026, 9, 15, 13, 0, 0, DateTimeKind.Utc),          // 9:00 Eastern
            EndUtc = new DateTime(2026, 9, 15, 13, 30, 0, DateTimeKind.Utc),
            TimeZoneId = "America/New_York",
            RecurrenceRule = "RRULE:FREQ=WEEKLY;INTERVAL=1;BYDAY=TU;UNTIL=20261013T235959",
        };

        var values = MapiCalendarOperations.Properties(tags, write, includeClass: true);

        values.Should().Contain(v => v.Tag == tags.Recurring && (bool)v.Value!);
        values.Should().Contain(v => v.Tag == tags.RecurrenceType && (uint)v.Value! == 2);
        values.Should().Contain(v => v.Tag == tags.ClipStart && (DateTime)v.Value! == new DateTime(2026, 9, 15));
        values.Should().Contain(v => v.Tag == tags.ClipEnd && (DateTime)v.Value! == new DateTime(2026, 10, 13));
        var blob = (byte[])values.First(v => v.Tag == tags.AppointmentRecur).Value!;
        var pattern = AppointmentRecurrence.Parse(blob);
        pattern.FirstStart.Should().Be(new DateTime(2026, 9, 15, 9, 0, 0), "wall clock in the appointment's zone");
        pattern.OccurrenceCount.Should().Be(5);

        var single = new MapiCalendarOperations.AppointmentWrite { StartUtc = write.StartUtc, EndUtc = write.EndUtc };
        MapiCalendarOperations.Properties(tags, single, includeClass: true).Should().Contain(v => v.Tag == tags.Recurring && !(bool)v.Value!);
        MapiCalendarOperations.Properties(tags, single, includeClass: true).Should().NotContain(v => v.Tag == tags.AppointmentRecur);
    }
}
