using System.Globalization;
using System.Text;
using Wino.Mapi.Wire;

namespace Wino.Mapi.Calendar;

/// <summary>
/// The write side of PidLidAppointmentRecur (MS-OXOCAL 2.2.1.44), the mirror of
/// <see cref="AppointmentRecurrence.Parse"/>: an RRULE from the app becomes a pattern, and a pattern
/// becomes the blob a series master carries. Dates and offsets are wall clock in the series' zone,
/// which the TZDEFINITION/TZREG written beside the blob names.
/// </summary>
public static class RecurrenceEncoder
{
    /// <summary>The EndDate the spec pins for a series that never ends or ends after N: 4500-08-31.</summary>
    public const uint NoEndDateMinutes = 0x5AE980DF;

    /// <summary>OccurrenceCount for a series with no end, as Outlook writes it.</summary>
    public const uint NoEndOccurrenceCount = 0x0000000A;

    private static readonly string[] DayNames = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    /// <summary>
    /// A pattern from an RRULE (with or without its "RRULE:" prefix) and the first occurrence's wall
    /// clock start and duration. UNTIL is read as a wall-clock date; COUNT as-is; neither means no end.
    /// A start that does not fall on the pattern moves forward to the first day that does.
    /// </summary>
    public static AppointmentRecurrence FromRRule(string rrule, DateTime firstStartWallClock, TimeSpan duration)
    {
        var text = rrule.Trim();
        if (text.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
            text = text.Substring(6);

        var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim().ToUpperInvariant(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);

        if (!parts.TryGetValue("FREQ", out var freq))
            throw new MapiFormatException("The recurrence rule has no FREQ.");
        var interval = parts.TryGetValue("INTERVAL", out var i) && uint.TryParse(i, out var iv) && iv > 0 ? iv : 1;
        var (mask, ordinal) = ParseByDay(parts.TryGetValue("BYDAY", out var byDay) ? byDay : null);
        if (parts.TryGetValue("BYSETPOS", out var setPos) && int.TryParse(setPos, out var pos) && ordinal == 0)
            ordinal = pos;

        var start = firstStartWallClock.Date;
        ushort frequency, pattern;
        uint period = interval, day = 0, nth = 0;

        switch (freq.ToUpperInvariant())
        {
            case "DAILY":
                frequency = AppointmentRecurrence.FrequencyDaily;
                if (mask != 0) { pattern = AppointmentRecurrence.PatternWeek; }          // "every weekday": weekly shape under the daily frequency
                else { pattern = AppointmentRecurrence.PatternDay; period = interval * 1440; }
                break;
            case "WEEKLY":
                frequency = AppointmentRecurrence.FrequencyWeekly;
                pattern = AppointmentRecurrence.PatternWeek;
                if (mask == 0) mask = 1u << (int)start.DayOfWeek;
                break;
            case "MONTHLY":
            case "YEARLY":
                var yearly = freq.Equals("YEARLY", StringComparison.OrdinalIgnoreCase);
                frequency = yearly ? AppointmentRecurrence.FrequencyYearly : AppointmentRecurrence.FrequencyMonthly;
                if (yearly) period = interval * 12;
                if (mask != 0)
                {
                    pattern = AppointmentRecurrence.PatternMonthNth;
                    nth = ordinal switch { < 0 => 5, 0 => (uint)((start.Day - 1) / 7 + 1), _ => (uint)Math.Min(ordinal, 5) };
                }
                else
                {
                    pattern = AppointmentRecurrence.PatternMonth;
                    day = parts.TryGetValue("BYMONTHDAY", out var md) && int.TryParse(md, out var mdv) ? (mdv < 0 ? 31u : (uint)mdv) : (uint)start.Day;
                }
                break;
            default:
                throw new MapiFormatException($"Unsupported recurrence FREQ '{freq}'.");
        }

        var firstDow = parts.TryGetValue("WKST", out var wkst) ? (uint)Math.Max(0, Array.IndexOf(DayNames, wkst.ToUpperInvariant())) : 0;

        var endType = AppointmentRecurrence.EndNever;
        uint count = NoEndOccurrenceCount;
        DateTime? endDate = null;
        if (parts.TryGetValue("COUNT", out var c) && uint.TryParse(c, out var cv) && cv > 0)
        {
            endType = AppointmentRecurrence.EndAfterCount;
            count = cv;
        }
        else if (parts.TryGetValue("UNTIL", out var until) && TryParseUntil(until, out var untilDate))
        {
            endType = AppointmentRecurrence.EndAfterDate;
            endDate = untilDate.Date;
        }

        var startOffset = firstStartWallClock.TimeOfDay;
        var shape = new AppointmentRecurrence
        {
            RecurFrequency = frequency, PatternType = pattern, Period = period, DayOfWeekMask = mask, Day = day, Nth = nth,
            EndType = endType, OccurrenceCount = count, FirstDayOfWeek = firstDow, StartDate = start, EndDate = endDate,
            StartTimeOffset = startOffset, EndTimeOffset = startOffset + duration,
        };

        // Align the start to the pattern, then settle the end fields the way Outlook writes them.
        var firstOccurrence = Occurrences(shape, 1).FirstOrDefault();
        if (firstOccurrence != default && firstOccurrence != start)
            shape = shape with { StartDate = firstOccurrence };

        if (endType == AppointmentRecurrence.EndAfterDate)
        {
            var all = Occurrences(shape, 100_000).ToList();
            shape = shape with { OccurrenceCount = (uint)Math.Max(1, all.Count), EndDate = all.Count > 0 ? all[^1] : shape.EndDate };
        }
        else if (endType == AppointmentRecurrence.EndAfterCount)
        {
            var all = Occurrences(shape, (int)count).ToList();
            shape = shape with { EndDate = all.Count > 0 ? all[^1] : shape.StartDate };
        }

        return shape;
    }

    /// <summary>ExceptionInfo OverrideFlags (2.2.1.44.3): which fields the exception overrides.</summary>
    private const ushort OverrideSubject = 0x0001, OverrideReminderDelta = 0x0004, OverrideReminderSet = 0x0008, OverrideLocation = 0x0010, OverrideBusyStatus = 0x0020, OverrideSubType = 0x0080;

    /// <summary>ChangeHighlight bits (2.2.1.44.2): what changed against the series, as Outlook records it.</summary>
    private const uint ChangeStart = 0x1, ChangeEnd = 0x2, ChangeLocation = 0x8, ChangeSubject = 0x10;

    /// <summary>
    /// The series with one occurrence removed: its original date joins the deleted dates, and any
    /// exception that occupied the date goes with it. Idempotent.
    /// </summary>
    public static AppointmentRecurrence WithDeletedOccurrence(AppointmentRecurrence r, DateTime originalStartWallClock)
    {
        var date = originalStartWallClock.Date;
        var deleted = r.DeletedInstanceDates.Where(d => d != date).Append(date).OrderBy(d => d).ToList();
        var modified = r.ModifiedInstanceDates.Where(d => d != date).OrderBy(d => d).ToList();
        var exceptions = r.Exceptions.Where(e => e.OriginalStart.Date != date).OrderBy(e => e.OriginalStart).ToList();
        return r with { DeletedInstanceDates = deleted, ModifiedInstanceDates = modified, Exceptions = exceptions };
    }

    /// <summary>
    /// The series with one occurrence put back as the pattern generates it: no exception, and its
    /// date on neither the modified nor the deleted list.
    /// </summary>
    public static AppointmentRecurrence WithoutException(AppointmentRecurrence r, DateTime originalStartWallClock)
    {
        var date = originalStartWallClock.Date;
        var deleted = r.DeletedInstanceDates.Where(d => d != date).OrderBy(d => d).ToList();
        var modified = r.ModifiedInstanceDates.Where(d => d != date).OrderBy(d => d).ToList();
        var exceptions = r.Exceptions.Where(e => e.OriginalStart.Date != date).OrderBy(e => e.OriginalStart).ToList();
        return r with { DeletedInstanceDates = deleted, ModifiedInstanceDates = modified, Exceptions = exceptions };
    }

    /// <summary>
    /// The series with one occurrence changed: the exception replaces any earlier one for the same
    /// original date, the date is listed as modified and (per 2.2.1.44.1) as deleted too, since the
    /// original slot is vacated and the exception re-occupies it.
    /// </summary>
    public static AppointmentRecurrence WithException(AppointmentRecurrence r, RecurrenceException exception)
    {
        var date = exception.OriginalStart.Date;
        var deleted = r.DeletedInstanceDates.Where(d => d != date).Append(date).OrderBy(d => d).ToList();
        var modified = r.ModifiedInstanceDates.Where(d => d != date).Append(date).OrderBy(d => d).ToList();
        var exceptions = r.Exceptions.Where(e => e.OriginalStart.Date != date).Append(exception).OrderBy(e => e.OriginalStart).ToList();
        return r with { DeletedInstanceDates = deleted, ModifiedInstanceDates = modified, Exceptions = exceptions };
    }

    /// <summary>The blob (2.2.1.44.1 + 2.2.1.44.5), with its exceptions as ExceptionInfo + ExtendedException pairs.</summary>
    public static byte[] Encode(AppointmentRecurrence r)
    {
        var w = new RopWriter();
        w.UInt16(0x3004); w.UInt16(0x3004);
        w.UInt16(r.RecurFrequency);
        w.UInt16(r.PatternType);
        w.UInt16(0);                                                     // CalendarType: Gregorian
        w.UInt32(FirstDateTime(r));
        w.UInt32(r.Period);
        w.UInt32(0);                                                     // SlidingFlag
        switch (r.PatternType)
        {
            case AppointmentRecurrence.PatternDay: break;
            case AppointmentRecurrence.PatternWeek: w.UInt32(r.DayOfWeekMask); break;
            case AppointmentRecurrence.PatternMonth:
            case AppointmentRecurrence.PatternMonthEnd: w.UInt32(r.Day); break;
            case AppointmentRecurrence.PatternMonthNth: w.UInt32(r.DayOfWeekMask); w.UInt32(r.Nth); break;
            default: throw new MapiFormatException($"Unhandled recurrence PatternType 0x{r.PatternType:X4}.");
        }
        w.UInt32(r.EndType);
        w.UInt32(r.OccurrenceCount);
        w.UInt32(r.FirstDayOfWeek);
        w.UInt32((uint)r.DeletedInstanceDates.Count);
        foreach (var d in r.DeletedInstanceDates) w.UInt32(AppointmentRecurrence.ToMinutes(d));
        w.UInt32((uint)r.ModifiedInstanceDates.Count);
        foreach (var d in r.ModifiedInstanceDates) w.UInt32(AppointmentRecurrence.ToMinutes(d));
        w.UInt32(AppointmentRecurrence.ToMinutes(r.StartDate));
        w.UInt32(r.EndType == AppointmentRecurrence.EndNever || r.EndType == AppointmentRecurrence.EndNeverAlt || r.EndDate is null
            ? NoEndDateMinutes
            : AppointmentRecurrence.ToMinutes(r.EndDate.Value));

        w.UInt32(0x3006); w.UInt32(0x3009);                              // ReaderVersion2, WriterVersion2
        w.UInt32((uint)r.StartTimeOffset.TotalMinutes);
        w.UInt32((uint)r.EndTimeOffset.TotalMinutes);

        var exceptions = r.Exceptions.OrderBy(e => e.OriginalStart).ToList();
        w.UInt16((ushort)exceptions.Count);
        foreach (var e in exceptions)
        {
            w.UInt32(AppointmentRecurrence.ToMinutes(e.Start));
            w.UInt32(AppointmentRecurrence.ToMinutes(e.End));
            w.UInt32(AppointmentRecurrence.ToMinutes(e.OriginalStart));
            w.UInt16(OverrideFlags(e));
            if (e.Subject is not null) WriteAnsi(w, e.Subject);
            if (e.Location is not null) WriteAnsi(w, e.Location);
            if (e.BusyStatus is { } busy) w.UInt32(busy);
            if (e.AllDay is { } allDay) w.UInt32(allDay ? 1u : 0u);
        }
        w.UInt32(0);                                                     // ReservedBlock1Size

        foreach (var e in exceptions)
        {
            w.UInt32(4);                                                 // ChangeHighlightSize
            w.UInt32(ChangeStart | ChangeEnd | (e.Subject is not null ? ChangeSubject : 0) | (e.Location is not null ? ChangeLocation : 0));
            w.UInt32(0);                                                 // ReservedBlockEE1Size
            if (e.Subject is not null || e.Location is not null)
            {
                w.UInt32(AppointmentRecurrence.ToMinutes(e.Start));
                w.UInt32(AppointmentRecurrence.ToMinutes(e.End));
                w.UInt32(AppointmentRecurrence.ToMinutes(e.OriginalStart));
                if (e.Subject is not null) { w.UInt16((ushort)e.Subject.Length); w.Bytes(Encoding.Unicode.GetBytes(e.Subject)); }
                if (e.Location is not null) { w.UInt16((ushort)e.Location.Length); w.Bytes(Encoding.Unicode.GetBytes(e.Location)); }
                w.UInt32(0);                                             // ReservedBlockEE2Size
            }
        }
        w.UInt32(0);                                                     // ReservedBlock2Size
        return w.ToArray();
    }

    private static ushort OverrideFlags(RecurrenceException e)
        => (ushort)((e.Subject is not null ? OverrideSubject : 0) | (e.Location is not null ? OverrideLocation : 0)
                  | (e.BusyStatus is not null ? OverrideBusyStatus : 0) | (e.AllDay is not null ? OverrideSubType : 0));

    /// <summary>The ANSI form of an override string: length + 1, length, bytes (Latin-1, as the reader takes it).</summary>
    private static void WriteAnsi(RopWriter w, string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        w.UInt16((ushort)(bytes.Length + 1));
        w.UInt16((ushort)bytes.Length);
        w.Bytes(bytes);
    }

    /// <summary>
    /// FirstDateTime (2.2.1.44.1): the start of the first period, in minutes from 1601 modulo the
    /// period, so the server can place occurrences without walking from the start.
    /// </summary>
    public static uint FirstDateTime(AppointmentRecurrence r)
    {
        var startMinutes = AppointmentRecurrence.ToMinutes(r.StartDate);
        switch (r.PatternType)
        {
            case AppointmentRecurrence.PatternDay:
                return r.Period == 0 ? 0 : startMinutes % r.Period;
            case AppointmentRecurrence.PatternWeek:
            {
                var weekStart = r.StartDate.AddDays(-DaysSinceWeekStart(r.StartDate, r.FirstDayOfWeek));
                var periodMinutes = Math.Max(1, r.Period) * 7 * 1440;
                return AppointmentRecurrence.ToMinutes(weekStart) % periodMinutes;
            }
            default:
            {
                var months = (r.StartDate.Year - 1601) * 12 + (r.StartDate.Month - 1);
                var period = (int)Math.Max(1, r.Period);
                var firstMonth = months - months % period;
                var first = new DateTime(1601 + firstMonth / 12, firstMonth % 12 + 1, 1);
                return AppointmentRecurrence.ToMinutes(first);
            }
        }
    }

    /// <summary>
    /// The occurrence dates of a pattern in order, at most <paramref name="max"/>, honouring COUNT
    /// and UNTIL. Walks day by day (bounded to two centuries) testing each against the pattern.
    /// </summary>
    public static IEnumerable<DateTime> Occurrences(AppointmentRecurrence r, int max)
    {
        var limit = r.EndType == AppointmentRecurrence.EndAfterDate && r.EndDate is { } end ? end : r.StartDate.AddYears(200);
        var remaining = r.EndType == AppointmentRecurrence.EndAfterCount ? (int)Math.Min(r.OccurrenceCount, (uint)max) : max;
        if (remaining <= 0) yield break;

        for (var day = r.StartDate; day <= limit; day = day.AddDays(1))
        {
            if (!Matches(r, day)) continue;
            yield return day;
            if (--remaining == 0) yield break;
        }
    }

    private static bool Matches(AppointmentRecurrence r, DateTime day)
    {
        switch (r.PatternType)
        {
            case AppointmentRecurrence.PatternDay:
                var everyDays = Math.Max(1, (int)(r.Period / 1440));
                return (day - r.StartDate).Days % everyDays == 0;

            case AppointmentRecurrence.PatternWeek:
            {
                if ((r.DayOfWeekMask & (1u << (int)day.DayOfWeek)) == 0) return false;
                var firstWeek = r.StartDate.AddDays(-DaysSinceWeekStart(r.StartDate, r.FirstDayOfWeek));
                var weeks = (day - firstWeek).Days / 7;
                return weeks % Math.Max(1, (int)r.Period) == 0;
            }

            case AppointmentRecurrence.PatternMonth:
            case AppointmentRecurrence.PatternMonthEnd:
            {
                if (!MonthOnPeriod(r, day)) return false;
                var daysInMonth = DateTime.DaysInMonth(day.Year, day.Month);
                var target = Math.Min((int)Math.Max(1, r.Day), daysInMonth);
                return day.Day == target;
            }

            case AppointmentRecurrence.PatternMonthNth:
            {
                if (!MonthOnPeriod(r, day)) return false;
                if ((r.DayOfWeekMask & (1u << (int)day.DayOfWeek)) == 0) return false;
                if (r.Nth >= 5)
                {
                    for (var later = day.AddDays(1); later.Month == day.Month; later = later.AddDays(1))
                        if ((r.DayOfWeekMask & (1u << (int)later.DayOfWeek)) != 0) return false;
                    return true;
                }
                var before = 0;
                for (var earlier = new DateTime(day.Year, day.Month, 1); earlier < day; earlier = earlier.AddDays(1))
                    if ((r.DayOfWeekMask & (1u << (int)earlier.DayOfWeek)) != 0) before++;
                return before == (int)r.Nth - 1;
            }

            default:
                return false;
        }
    }

    private static bool MonthOnPeriod(AppointmentRecurrence r, DateTime day)
    {
        var months = (day.Year - r.StartDate.Year) * 12 + (day.Month - r.StartDate.Month);
        return months >= 0 && months % Math.Max(1, (int)r.Period) == 0;
    }

    private static int DaysSinceWeekStart(DateTime day, uint firstDow)
        => (((int)day.DayOfWeek - (int)(firstDow % 7)) + 7) % 7;

    /// <summary>BYDAY as a mask plus the ordinal ("2TU" = second Tuesday, "-1FR" = last Friday) when every entry carries the same one.</summary>
    private static (uint Mask, int Ordinal) ParseByDay(string? byDay)
    {
        if (string.IsNullOrWhiteSpace(byDay)) return (0, 0);
        uint mask = 0;
        var ordinal = 0;
        foreach (var raw in byDay.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = raw.Trim().ToUpperInvariant();
            var name = entry.Length >= 2 ? entry.Substring(entry.Length - 2) : entry;
            var index = Array.IndexOf(DayNames, name);
            if (index < 0) continue;
            mask |= 1u << index;
            if (entry.Length > 2 && int.TryParse(entry.Substring(0, entry.Length - 2), out var ord))
                ordinal = ord;
        }
        return (mask, ordinal);
    }

    private static bool TryParseUntil(string value, out DateTime date)
    {
        var v = value.Trim().TrimEnd('Z', 'z');
        return DateTime.TryParseExact(v, ["yyyyMMdd'T'HHmmss", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
