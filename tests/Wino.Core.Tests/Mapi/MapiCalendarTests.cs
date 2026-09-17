using Wino.Mapi.Calendar;
using Wino.Mapi.Wire;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>Calendar: the appointment recurrence blob (MS-OXOCAL 2.2.1.44) and its RRULE.</summary>
public class MapiCalendarTests
{
    private static byte[] Blob(ushort frequency, ushort patternType, uint period, uint[] specific, uint endType, uint count, uint firstDow,
        DateTime start, DateTime? end, uint startOffsetMinutes, uint endOffsetMinutes, uint[]? deleted = null, (DateTime Original, DateTime Start, DateTime End, string Subject)[]? exceptions = null)
    {
        var w = new RopWriter();
        w.UInt16(0x3004); w.UInt16(0x3004);
        w.UInt16(frequency); w.UInt16(patternType); w.UInt16(0); w.UInt32(0); w.UInt32(period); w.UInt32(0);
        foreach (var v in specific) w.UInt32(v);
        w.UInt32(endType); w.UInt32(count); w.UInt32(firstDow);
        var del = deleted ?? [];
        w.UInt32((uint)del.Length); foreach (var d in del) w.UInt32(d);
        var mods = exceptions ?? [];
        w.UInt32((uint)mods.Length); foreach (var m in mods) w.UInt32(AppointmentRecurrence.ToMinutes(m.Original.Date));
        w.UInt32(AppointmentRecurrence.ToMinutes(start));
        w.UInt32(end is { } e ? AppointmentRecurrence.ToMinutes(e) : 0x5AE980DF);
        w.UInt32(0x3006); w.UInt32(0x3009);
        w.UInt32(startOffsetMinutes); w.UInt32(endOffsetMinutes);
        w.UInt16((ushort)mods.Length);
        foreach (var m in mods)
        {
            w.UInt32(AppointmentRecurrence.ToMinutes(m.Start)); w.UInt32(AppointmentRecurrence.ToMinutes(m.End)); w.UInt32(AppointmentRecurrence.ToMinutes(m.Original));
            w.UInt16(0x0001);                                    // subject override
            var ansi = System.Text.Encoding.Latin1.GetBytes(m.Subject);
            w.UInt16((ushort)(ansi.Length + 1)); w.UInt16((ushort)ansi.Length); w.Bytes(ansi);
        }
        w.UInt32(0);                                             // ReservedBlock1
        foreach (var m in mods)
        {
            w.UInt32(0);                                         // ChangeHighlight size (writer 0x3009)
            w.UInt32(0);                                         // ReservedBlockEE1
            w.UInt32(0); w.UInt32(0); w.UInt32(0);
            var wide = m.Subject + " (wide)";
            w.UInt16((ushort)wide.Length); w.Bytes(System.Text.Encoding.Unicode.GetBytes(wide));
            w.UInt32(0);                                         // ReservedBlockEE2
        }
        w.UInt32(0);                                             // ReservedBlock2
        return w.ToArray();
    }

    [Fact]
    public void Weekly_TwoDays_UntilDate_WithDeletedAndModifiedInstances()
    {
        var start = new DateTime(2026, 9, 7);                    // a Monday
        var until = new DateTime(2026, 12, 28);
        var deletedDate = AppointmentRecurrence.ToMinutes(new DateTime(2026, 9, 14));
        var original = new DateTime(2026, 9, 16, 9, 0, 0);
        var blob = Blob(AppointmentRecurrence.FrequencyWeekly, AppointmentRecurrence.PatternWeek, 1, [0x02 | 0x08], AppointmentRecurrence.EndAfterDate, 0, 1,
            start, until, 9 * 60, 10 * 60, [deletedDate], [(original, original.AddHours(2), original.AddHours(3), "Moved standup")]);

        var recurrence = AppointmentRecurrence.Parse(blob);

        recurrence.FirstStart.Should().Be(new DateTime(2026, 9, 7, 9, 0, 0));
        recurrence.Duration.Should().Be(TimeSpan.FromHours(1));
        recurrence.DeletedInstanceDates.Should().Equal(new DateTime(2026, 9, 14));
        recurrence.ModifiedInstanceDates.Should().Equal(new DateTime(2026, 9, 16));
        recurrence.Exceptions.Should().ContainSingle();
        recurrence.Exceptions[0].Start.Should().Be(original.AddHours(2));
        recurrence.Exceptions[0].Subject.Should().Be("Moved standup (wide)");
        recurrence.ToRRule().Should().Be("FREQ=WEEKLY;INTERVAL=1;BYDAY=MO,WE;UNTIL=20261228T090000;WKST=MO");
    }

    [Fact]
    public void Daily_EveryWeekday_Count_And_MonthlyNth_And_Yearly()
    {
        var start = new DateTime(2026, 9, 7);
        var weekday = AppointmentRecurrence.Parse(Blob(AppointmentRecurrence.FrequencyDaily, AppointmentRecurrence.PatternWeek, 1, [0x3E], AppointmentRecurrence.EndAfterCount, 10, 0, start, null, 8 * 60, 8 * 60 + 30));
        weekday.ToRRule().Should().Be("FREQ=WEEKLY;INTERVAL=1;BYDAY=MO,TU,WE,TH,FR;COUNT=10;WKST=SU");

        var daily = AppointmentRecurrence.Parse(Blob(AppointmentRecurrence.FrequencyDaily, AppointmentRecurrence.PatternDay, 2880, [], AppointmentRecurrence.EndNever, 0, 0, start, null, 0, 1440));
        daily.ToRRule().Should().Be("FREQ=DAILY;INTERVAL=2;WKST=SU");
        daily.Duration.Should().Be(TimeSpan.FromDays(1));

        var monthlyNth = AppointmentRecurrence.Parse(Blob(AppointmentRecurrence.FrequencyMonthly, AppointmentRecurrence.PatternMonthNth, 1, [0x20, 5], AppointmentRecurrence.EndNever, 0, 0, start, null, 60, 120));
        monthlyNth.ToRRule().Should().Be("FREQ=MONTHLY;INTERVAL=1;BYDAY=FR;BYSETPOS=-1;WKST=SU");

        var yearly = AppointmentRecurrence.Parse(Blob(AppointmentRecurrence.FrequencyYearly, AppointmentRecurrence.PatternMonth, 12, [31], AppointmentRecurrence.EndNever, 0, 0, new DateTime(2026, 10, 31), null, 60, 120));
        yearly.ToRRule().Should().Be("FREQ=YEARLY;INTERVAL=1;BYMONTHDAY=-1;BYMONTH=10;WKST=SU");
    }
}
