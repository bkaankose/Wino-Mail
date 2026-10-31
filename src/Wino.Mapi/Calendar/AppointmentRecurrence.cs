using System.Text;
using Wino.Mapi.Wire;

namespace Wino.Mapi.Calendar;

/// <summary>A modified instance of a series (MS-OXOCAL 2.2.1.44.4 ExceptionInfo), with the fields this client uses.</summary>
public sealed record RecurrenceException(
    DateTime OriginalStart,
    DateTime Start,
    DateTime End,
    string? Subject,
    string? Location,
    uint? BusyStatus,
    bool? AllDay);

/// <summary>
/// PidLidAppointmentRecur (MS-OXOCAL 2.2.1.44), decoded: the pattern, its bounds, the deleted and
/// modified instance dates, and the exception overrides. Dates in the blob are minutes since
/// 1601-01-01 in the series' own time zone (floating wall-clock), and are surfaced that way as
/// DateTimeKind.Unspecified; the caller pairs them with the appointment's time zone.
/// </summary>
public sealed record AppointmentRecurrence
{
    public const ushort FrequencyDaily = 0x200A;
    public const ushort FrequencyWeekly = 0x200B;
    public const ushort FrequencyMonthly = 0x200C;
    public const ushort FrequencyYearly = 0x200D;

    public const ushort PatternDay = 0x0000;
    public const ushort PatternWeek = 0x0001;
    public const ushort PatternMonth = 0x0002;
    public const ushort PatternMonthNth = 0x0003;
    public const ushort PatternMonthEnd = 0x0004;

    public const uint EndAfterDate = 0x00002021;
    public const uint EndAfterCount = 0x00002022;
    public const uint EndNever = 0x00002023;
    public const uint EndNeverAlt = 0xFFFFFFFF;

    private static readonly DateTime Epoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    public ushort RecurFrequency { get; init; }
    public ushort PatternType { get; init; }
    public uint Period { get; init; }
    public uint DayOfWeekMask { get; init; }
    public uint Day { get; init; }
    public uint Nth { get; init; }
    public uint EndType { get; init; }
    public uint OccurrenceCount { get; init; }
    public uint FirstDayOfWeek { get; init; }
    public DateTime StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public TimeSpan StartTimeOffset { get; init; }
    public TimeSpan EndTimeOffset { get; init; }
    public List<DateTime> DeletedInstanceDates { get; init; } = [];
    public List<DateTime> ModifiedInstanceDates { get; init; } = [];
    public List<RecurrenceException> Exceptions { get; init; } = [];

    /// <summary>The first occurrence's wall-clock start.</summary>
    public DateTime FirstStart => StartDate + StartTimeOffset;

    /// <summary>The occurrence duration; the blob carries offsets from midnight, which can cross it.</summary>
    public TimeSpan Duration => EndTimeOffset >= StartTimeOffset ? EndTimeOffset - StartTimeOffset : EndTimeOffset + TimeSpan.FromDays(1) - StartTimeOffset;

    public static DateTime FromMinutes(uint minutes) => Epoch.AddMinutes(minutes);
    public static uint ToMinutes(DateTime value) => (uint)(value - Epoch).TotalMinutes;

    public static AppointmentRecurrence Parse(ReadOnlyMemory<byte> blob)
    {
        var r = new RopReader(blob);

        // RecurrencePattern (2.2.1.44.1)
        var readerVersion = r.UInt16();
        var writerVersion = r.UInt16();
        if (readerVersion != 0x3004 || writerVersion != 0x3004)
            throw new MapiFormatException($"Unexpected RecurrencePattern versions 0x{readerVersion:X4}/0x{writerVersion:X4}.");

        var frequency = r.UInt16();
        var patternType = r.UInt16();
        r.UInt16();                                          // CalendarType
        r.UInt32();                                          // FirstDateTime
        var period = r.UInt32();
        r.UInt32();                                          // SlidingFlag

        uint mask = 0, day = 0, nth = 0;
        switch (patternType)
        {
            case PatternDay: break;
            case PatternWeek: mask = r.UInt32(); break;
            case PatternMonth:
            case PatternMonthEnd:
            case 0x000A: case 0x000C:                        // Hijri / Chinese month variants share the layout
                day = r.UInt32(); break;
            case PatternMonthNth:
            case 0x000B: case 0x000D:
                mask = r.UInt32(); nth = r.UInt32(); break;
            default:
                throw new MapiFormatException($"Unhandled recurrence PatternType 0x{patternType:X4}.");
        }

        var endType = r.UInt32();
        var occurrenceCount = r.UInt32();
        var firstDow = r.UInt32();

        var deletedCount = r.UInt32();
        var deleted = new List<DateTime>((int)Math.Min(deletedCount, 4096));
        for (var i = 0; i < deletedCount; i++) deleted.Add(FromMinutes(r.UInt32()));

        var modifiedCount = r.UInt32();
        var modified = new List<DateTime>((int)Math.Min(modifiedCount, 4096));
        for (var i = 0; i < modifiedCount; i++) modified.Add(FromMinutes(r.UInt32()));

        var startDate = FromMinutes(r.UInt32());
        var endMinutes = r.UInt32();
        DateTime? endDate = endType is EndAfterDate ? FromMinutes(endMinutes) : null;

        // AppointmentRecurrencePattern (2.2.1.44.5)
        r.UInt32();                                          // ReaderVersion2 (0x3006)
        var writerVersion2 = r.UInt32();
        var startOffset = TimeSpan.FromMinutes(r.UInt32());
        var endOffset = TimeSpan.FromMinutes(r.UInt32());
        var exceptionCount = r.UInt16();

        var infos = new List<(DateTime Start, DateTime End, DateTime Original, ushort Flags, string? Subject, string? Location, uint? Busy, bool? AllDay)>(exceptionCount);
        for (var i = 0; i < exceptionCount; i++)
        {
            var start = FromMinutes(r.UInt32());
            var end = FromMinutes(r.UInt32());
            var original = FromMinutes(r.UInt32());
            var flags = r.UInt16();
            string? subject = null, location = null;
            uint? busy = null;
            bool? allDay = null;

            if ((flags & 0x0001) != 0) { r.UInt16(); var len = r.UInt16(); subject = Encoding.Latin1.GetString(r.Bytes(len).Span); }
            if ((flags & 0x0002) != 0) r.UInt32();          // MeetingType
            if ((flags & 0x0004) != 0) r.UInt32();          // ReminderDelta
            if ((flags & 0x0008) != 0) r.UInt32();          // ReminderSet
            if ((flags & 0x0010) != 0) { r.UInt16(); var len = r.UInt16(); location = Encoding.Latin1.GetString(r.Bytes(len).Span); }
            if ((flags & 0x0020) != 0) busy = r.UInt32();
            if ((flags & 0x0040) != 0) r.UInt32();          // Attachment
            if ((flags & 0x0080) != 0) allDay = r.UInt32() != 0;
            if ((flags & 0x0100) != 0) r.UInt32();          // AppointmentColor

            infos.Add((start, end, original, flags, subject, location, busy, allDay));
        }

        var reserved1 = r.UInt32();
        r.Bytes((int)reserved1);

        // ExtendedException (2.2.1.44.4): Unicode subject/location, which supersede the ANSI ones.
        var exceptions = new List<RecurrenceException>(exceptionCount);
        for (var i = 0; i < exceptionCount; i++)
        {
            var info = infos[i];
            string? subject = info.Subject, location = info.Location;

            if (r.Remaining > 0)
            {
                if (writerVersion2 >= 0x3009)
                {
                    var highlightSize = r.UInt32();
                    r.Bytes((int)highlightSize);
                }

                var reservedEe1 = r.UInt32();
                r.Bytes((int)reservedEe1);

                if ((info.Flags & 0x0011) != 0)
                {
                    r.UInt32(); r.UInt32(); r.UInt32();     // StartDateTime, EndDateTime, OriginalStartDate again
                    if ((info.Flags & 0x0001) != 0) { var len = r.UInt16(); subject = Encoding.Unicode.GetString(r.Bytes(len * 2).Span); }
                    if ((info.Flags & 0x0010) != 0) { var len = r.UInt16(); location = Encoding.Unicode.GetString(r.Bytes(len * 2).Span); }
                    var reservedEe2 = r.UInt32();
                    r.Bytes((int)reservedEe2);
                }
            }

            exceptions.Add(new RecurrenceException(info.Original, info.Start, info.End, subject, location, info.Busy, info.AllDay));
        }

        return new AppointmentRecurrence
        {
            RecurFrequency = frequency,
            PatternType = patternType,
            Period = period,
            DayOfWeekMask = mask,
            Day = day,
            Nth = nth,
            EndType = endType,
            OccurrenceCount = occurrenceCount,
            FirstDayOfWeek = firstDow,
            StartDate = startDate,
            EndDate = endDate,
            StartTimeOffset = startOffset,
            EndTimeOffset = endOffset,
            DeletedInstanceDates = deleted,
            ModifiedInstanceDates = modified,
            Exceptions = exceptions,
        };
    }

    /// <summary>
    /// The pattern as an RFC 5545 RRULE. UNTIL is emitted as a floating local date-time (the series'
    /// wall clock), which is what the expansion pairs with the series time zone. Deleted and modified
    /// instances are not part of the rule; the caller applies them to the expanded occurrences.
    /// </summary>
    public string ToRRule()
    {
        var parts = new List<string>();
        var startMonth = StartDate.Month;

        switch (RecurFrequency)
        {
            case FrequencyDaily:
                if (PatternType == PatternWeek)
                {
                    // "Every weekday": Outlook encodes it as a weekly pattern under the daily frequency.
                    parts.Add("FREQ=WEEKLY");
                    parts.Add("INTERVAL=1");
                    parts.Add("BYDAY=" + ByDay(DayOfWeekMask));
                }
                else
                {
                    parts.Add("FREQ=DAILY");
                    parts.Add($"INTERVAL={Math.Max(1, Period / 1440)}");
                }
                break;

            case FrequencyWeekly:
                parts.Add("FREQ=WEEKLY");
                parts.Add($"INTERVAL={Math.Max(1, Period)}");
                parts.Add("BYDAY=" + ByDay(DayOfWeekMask));
                break;

            case FrequencyMonthly:
            case FrequencyYearly:
                var yearly = RecurFrequency == FrequencyYearly;
                parts.Add(yearly ? "FREQ=YEARLY" : "FREQ=MONTHLY");
                parts.Add($"INTERVAL={Math.Max(1, yearly ? Period / 12 : Period)}");
                if (PatternType is PatternMonth or PatternMonthEnd or 0x000A or 0x000C)
                    parts.Add("BYMONTHDAY=" + (Day >= 31 ? "-1" : Day.ToString()));
                else
                {
                    parts.Add("BYDAY=" + ByDay(DayOfWeekMask));
                    parts.Add("BYSETPOS=" + (Nth >= 5 ? "-1" : Nth.ToString()));
                }
                if (yearly)
                    parts.Add($"BYMONTH={startMonth}");
                break;

            default:
                throw new MapiFormatException($"Unhandled RecurFrequency 0x{RecurFrequency:X4}.");
        }

        if (EndType == EndAfterCount && OccurrenceCount > 0)
            parts.Add($"COUNT={OccurrenceCount}");
        else if (EndType == EndAfterDate && EndDate is { } end)
            parts.Add("UNTIL=" + (end + StartTimeOffset).ToString("yyyyMMdd'T'HHmmss"));

        if (FirstDayOfWeek < 7)
            parts.Add("WKST=" + DayNames[FirstDayOfWeek]);

        return string.Join(";", parts);
    }

    private static readonly string[] DayNames = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    private static string ByDay(uint mask)
    {
        var days = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            if ((mask & (1u << i)) != 0)
                days.Add(DayNames[i]);
        }

        return days.Count == 0 ? "MO" : string.Join(",", days);
    }
}
