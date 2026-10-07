using System;
using System.Globalization;

namespace Wino.Core.Domain.Models.Calendar;

public static class CalendarAccessibilityFormatter
{
    /// <summary>
    /// All-day end dates are exclusive. Announce occupied dates without suggesting
    /// a zero-length midnight meeting, and retain the full dates for timed events.
    /// </summary>
    public static string FormatPeriod(DateTime start, DateTime end, bool isAllDay, CalendarSettings settings)
    {
        var culture = settings?.CultureInfo ?? CultureInfo.CurrentCulture;
        if (isAllDay)
        {
            var lastDate = end > start ? end.AddTicks(-1).Date : start.Date;
            var dates = start.Date == lastDate
                ? start.ToString("D", culture)
                : $"{start.ToString("D", culture)} – {lastDate.ToString("D", culture)}";
            return $"{dates}, {Translator.CalendarItemAllDay}";
        }

        var startTime = settings?.GetTimeString(start.TimeOfDay) ?? start.ToString("t", culture);
        var endTime = settings?.GetTimeString(end.TimeOfDay) ?? end.ToString("t", culture);
        return $"{start.ToString("D", culture)} {startTime} – {end.ToString("D", culture)} {endTime}";
    }
}
