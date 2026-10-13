#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Domain.Presentation;

/// <summary>Why an event tile refuses a drag to move or resize it.</summary>
public enum CalendarDragRefusal
{
    None,
    /// <summary>Locked, or organized by someone else (CalendarDragDropMoveNotAllowed* info bar).</summary>
    NotAllowed,
    /// <summary>The calendar is read-only (CalendarReadOnly_* info bar).</summary>
    ReadOnlyCalendar,
    /// <summary>A pending operation holds the item, or there is no editable item; the drag is ignored silently.</summary>
    Busy
}

/// <summary>The parts of an RRULE the event details need.</summary>
public sealed record CalendarRecurrenceRule(CalendarItemRecurrenceFrequency Frequency, int Interval, IReadOnlyList<DayOfWeek> Days, DateTimeOffset? Until);

/// <summary>
/// Reads FREQ, INTERVAL, BYDAY and UNTIL from a stored recurrence (RRULE lines separated by
/// <see cref="Constants.CalendarEventRecurrenceRuleSeperator"/> or new lines, with or without the RRULE: prefix).
/// </summary>
public static class CalendarRecurrenceRuleParser
{
    public static bool TryParse(string? recurrence, out CalendarRecurrenceRule? rule)
    {
        rule = null;
        if (string.IsNullOrWhiteSpace(recurrence)) return false;

        foreach (var rawLine in recurrence.Split([Constants.CalendarEventRecurrenceRuleSeperator, "\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            int colon = line.IndexOf(':');
            if (colon >= 0)
            {
                if (!line[..colon].StartsWith("RRULE", StringComparison.OrdinalIgnoreCase)) continue;
                line = line[(colon + 1)..];
            }

            if (TryParseRule(line, out rule)) return true;
        }

        return false;
    }

    private static bool TryParseRule(string line, out CalendarRecurrenceRule? rule)
    {
        rule = null;
        CalendarItemRecurrenceFrequency? frequency = null;
        int interval = 1;
        var days = new List<DayOfWeek>();
        DateTimeOffset? until = null;

        foreach (var part in line.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=');
            if (equals <= 0) continue;

            var key = part[..equals].Trim().ToUpperInvariant();
            var value = part[(equals + 1)..].Trim();

            switch (key)
            {
                case "FREQ":
                    frequency = value.ToUpperInvariant() switch
                    {
                        "DAILY" => CalendarItemRecurrenceFrequency.Daily,
                        "WEEKLY" => CalendarItemRecurrenceFrequency.Weekly,
                        "MONTHLY" => CalendarItemRecurrenceFrequency.Monthly,
                        "YEARLY" => CalendarItemRecurrenceFrequency.Yearly,
                        _ => null
                    };
                    break;
                case "INTERVAL" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInterval) && parsedInterval > 0:
                    interval = parsedInterval;
                    break;
                case "BYDAY":
                    foreach (var day in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (TryParseDay(day.Trim(), out var dayOfWeek) && !days.Contains(dayOfWeek)) days.Add(dayOfWeek);
                    }
                    break;
                case "UNTIL":
                    until = ParseUntil(value);
                    break;
            }
        }

        if (frequency is not { } resolved) return false;

        rule = new CalendarRecurrenceRule(resolved, interval, days.ToArray(), until);
        return true;
    }

    private static bool TryParseDay(string value, out DayOfWeek day)
    {
        // Monthly and yearly rules may prefix an ordinal ("1MO", "-1FR").
        var code = value.Length >= 2 ? value[^2..].ToUpperInvariant() : string.Empty;
        (bool ok, DayOfWeek result) = code switch
        {
            "MO" => (true, DayOfWeek.Monday),
            "TU" => (true, DayOfWeek.Tuesday),
            "WE" => (true, DayOfWeek.Wednesday),
            "TH" => (true, DayOfWeek.Thursday),
            "FR" => (true, DayOfWeek.Friday),
            "SA" => (true, DayOfWeek.Saturday),
            "SU" => (true, DayOfWeek.Sunday),
            _ => (false, DayOfWeek.Sunday)
        };

        day = result;
        return ok;
    }

    private static DateTimeOffset? ParseUntil(string value)
    {
        string[] formats = ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd"];
        return DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc))
            : null;
    }
}

/// <summary>Shared text and editing rules for calendar event details and tiles.</summary>
public static class CalendarEventDisplay
{
    /// <summary>True when the item belongs to a read-only calendar (subscribed, birthdays, shared without write access).</summary>
    public static bool IsReadOnly(ICalendarItem? item) => item?.AssignedCalendar?.IsReadOnly == true;

    /// <summary>
    /// Why a tile cannot be dragged, mirroring the checks of the calendar page's move command:
    /// no editable item or a busy one is ignored, a locked event is not allowed, a read-only calendar refuses.
    /// </summary>
    public static CalendarDragRefusal GetDragRefusal(CalendarItem? item, bool isBusy)
    {
        if (item is null) return CalendarDragRefusal.Busy;
        if (!item.CanChangeStartAndEndDate) return CalendarDragRefusal.NotAllowed;
        if (IsReadOnly(item)) return CalendarDragRefusal.ReadOnlyCalendar;
        return isBusy ? CalendarDragRefusal.Busy : CalendarDragRefusal.None;
    }

    /// <summary>
    /// The event details date line: all-day events show their day or day range with "All day", multi-day
    /// events both ends with times, other events the day with the start and end time.
    /// </summary>
    public static string FormatDateRange(DateTime start, DateTime end, bool isAllDay, bool isMultiDay, CalendarSettings? settings)
    {
        if (settings is null) return string.Empty;

        var culture = settings.CultureInfo;
        var timeFormat = DateTimeDisplayFormatter.GetTimeFormat(settings.DayHeaderDisplayType);

        if (isAllDay)
        {
            var lastDay = end.AddDays(-1);
            return lastDay.Date <= start.Date
                ? $"{start.ToString("dddd, dd MMMM", culture)} · {Translator.CalendarItemAllDay}"
                : $"{start.ToString("dd MMMM", culture)} – {lastDay.ToString("dd MMMM", culture)} · {Translator.CalendarItemAllDay}";
        }

        if (isMultiDay)
            return $"{start.ToString($"dd MMMM ddd {timeFormat}", culture)} - {end.ToString($"dd MMMM ddd {timeFormat}", culture)}";

        return $"{start.ToString($"dddd, dd MMMM {timeFormat}", culture)} - {end.ToString(timeFormat, culture)}";
    }

    /// <summary>
    /// The recurrence sentence (<see cref="CalendarRecurrenceSummaryFormatter"/>); "Recurring" when the stored rule
    /// cannot be read, empty for a single event.
    /// </summary>
    public static string FormatRecurrence(string? recurrence, DateTime start, DateTime end, bool isAllDay, CalendarSettings? settings)
    {
        if (string.IsNullOrWhiteSpace(recurrence)) return string.Empty;
        if (settings is null || !CalendarRecurrenceRuleParser.TryParse(recurrence, out var rule) || rule is null)
            return Translator.CalendarEventCompose_Recurring;

        return CalendarRecurrenceSummaryFormatter.BuildSummary(
            isRecurring: true,
            effectiveStart: start,
            effectiveEnd: end,
            isAllDay: isAllDay,
            settings: settings,
            interval: rule.Interval,
            frequency: rule.Frequency,
            daysOfWeek: (DayOfWeek[])[.. rule.Days],
            recurrenceEndDate: rule.Until);
    }

    public static string GetAttendeeStatusText(AttendeeStatus status) => status switch
    {
        AttendeeStatus.Accepted => Translator.CalendarAttendeeStatus_Accepted,
        AttendeeStatus.Declined => Translator.CalendarAttendeeStatus_Declined,
        AttendeeStatus.Tentative => Translator.CalendarAttendeeStatus_Tentative,
        _ => Translator.CalendarAttendeeStatus_NeedsAction
    };
}
