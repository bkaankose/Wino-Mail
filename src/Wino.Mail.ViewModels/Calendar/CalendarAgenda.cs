#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.ViewModels;

/// <summary>One day of an agenda list: the date and its events, all-day and multi-day events first, then by start.</summary>
public sealed record CalendarAgendaDay(DateOnly Date, IReadOnlyList<CalendarItemViewModel> Items);

/// <summary>
/// The phone calendar's agenda and month-dot rules (iPhone agenda list, Android agenda and month grid): which loaded
/// events fall on a day and in what order, the time and duration texts of an agenda row, the day header and the colour
/// dots of a month cell. Platform-neutral; the heads only draw.
/// </summary>
public static class CalendarAgenda
{
    /// <summary>The most dots a month cell shows.</summary>
    public const int MaxDayDots = 3;

    /// <summary>
    /// The agenda days of <paramref name="dates"/>. Days without events stay in the list (they read "No events") unless
    /// <paramref name="skipEmptyDays"/> is set, which the month agenda uses.
    /// </summary>
    public static List<CalendarAgendaDay> BuildDays(IEnumerable<DateOnly> dates, IEnumerable<CalendarItemViewModel> items, bool skipEmptyDays)
    {
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(items);

        var list = items as IList<CalendarItemViewModel> ?? items.ToList();
        var days = new List<CalendarAgendaDay>();
        foreach (var date in dates)
        {
            var dayItems = ItemsOn(date, list);
            if (dayItems.Count == 0 && skipEmptyDays) continue;
            days.Add(new CalendarAgendaDay(date, dayItems));
        }

        return days;
    }

    /// <summary>The events that touch <paramref name="date"/>: all-day and multi-day first, then by start time.</summary>
    public static List<CalendarItemViewModel> ItemsOn(DateOnly date, IEnumerable<CalendarItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var start = date.ToDateTime(TimeOnly.MinValue);
        var end = start.AddDays(1);
        return items.Where(item => item.StartDate < end && item.EndDate > start || item.StartDate == start)
            .OrderBy(item => item.IsAllDayEvent || item.IsMultiDayEvent ? 0 : 1)
            .ThenBy(item => item.StartDate)
            .ToList();
    }

    /// <summary>True when the event fills <paramref name="date"/> (an all-day event, or a multi-day event on a middle day).</summary>
    public static bool IsAllDayOn(CalendarItemViewModel item, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(item);

        var day = date.ToDateTime(TimeOnly.MinValue);
        return item.IsAllDayEvent || item.IsMultiDayEvent && (item.StartDate.Date < day || item.EndDate.Date > day);
    }

    /// <summary>"10:00 – 10:30" in the user's time format.</summary>
    public static string TimeRangeText(DateTime start, DateTime end, CalendarSettings? settings)
        => settings is null
            ? $"{start:t} – {end:t}"
            : $"{settings.GetTimeString(start.TimeOfDay)} – {settings.GetTimeString(end.TimeOfDay)}";

    /// <summary>An agenda row's time: "All day", or the time range.</summary>
    public static string TimeText(CalendarItemViewModel item, DateOnly date, CalendarSettings? settings)
        => IsAllDayOn(item, date) ? Translator.QuickEventDialog_IsAllDay : TimeRangeText(item.StartDate, item.EndDate, settings);

    /// <summary>An agenda row's leading time: "All day", or the start time (the duration goes below it).</summary>
    public static string StartText(CalendarItemViewModel item, DateOnly date, CalendarSettings? settings)
    {
        if (IsAllDayOn(item, date)) return Translator.QuickEventDialog_IsAllDay;
        return settings is null ? item.StartDate.ToString("t", CultureInfo.CurrentCulture) : settings.GetTimeString(item.StartDate.TimeOfDay);
    }

    /// <summary>An agenda row's duration under its start time ("30 min", "1 h", "1 h 30 min"); empty for all-day rows.</summary>
    public static string DurationText(CalendarItemViewModel item, DateOnly date)
        => IsAllDayOn(item, date) ? string.Empty : DurationText(item.EndDate - item.StartDate);

    /// <summary>A duration in days, hours and minutes, as the event composer writes it.</summary>
    public static string DurationText(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return string.Empty;

        var parts = new List<string>(3);
        if (duration.Days > 0)
        {
            parts.Add(duration.Days == 1
                ? Translator.CalendarEventCompose_DurationDay
                : string.Format(Translator.CalendarEventCompose_DurationDays, duration.Days));
        }

        if (duration.Hours > 0) parts.Add(string.Format(Translator.CalendarEventCompose_DurationHours, duration.Hours));
        if (duration.Minutes > 0) parts.Add(string.Format(Translator.CalendarEventCompose_DurationMinutes, duration.Minutes));
        return string.Join(" ", parts);
    }

    /// <summary>A day header: "Today · Wednesday, 14 October" for today, else "Thursday, 15 October".</summary>
    public static string DayTitle(DateOnly date, DateOnly today, CultureInfo? culture)
    {
        var text = date.ToDateTime(TimeOnly.MinValue).ToString("dddd, d MMMM", culture ?? CultureInfo.CurrentCulture);
        return date == today ? $"{Translator.Today} · {text}" : text;
    }

    /// <summary>The month title of a date ("October 2026"), the phone calendar's top app bar title.</summary>
    public static string MonthTitle(DateOnly date, CultureInfo? culture)
        => date.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", culture ?? CultureInfo.CurrentCulture);

    /// <summary>The colour of an event: its own colour, else its calendar's, else null (the accent).</summary>
    public static string? ColorHex(CalendarItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var custom = item.CalendarItem.CustomEventColorHex;
        return string.IsNullOrWhiteSpace(custom) ? item.AssignedCalendar?.BackgroundColorHex : custom;
    }

    /// <summary>
    /// The dots of a month cell: one per distinct event colour on that day, in agenda order, at most
    /// <see cref="MaxDayDots"/>. A null entry stands for the accent colour.
    /// </summary>
    public static List<string?> DayDots(DateOnly date, IEnumerable<CalendarItemViewModel> items)
    {
        var dots = new List<string?>(MaxDayDots);
        foreach (var item in ItemsOn(date, items))
        {
            var color = ColorHex(item);
            if (dots.Any(existing => string.Equals(existing, color, StringComparison.OrdinalIgnoreCase))) continue;
            dots.Add(color);
            if (dots.Count == MaxDayDots) break;
        }

        return dots;
    }
}
