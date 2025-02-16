using System;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Domain.Extensions;

public static class DateTimeExtensions
{
    /// <summary>
    /// Returns a date range for the month of the given date.
    /// </summary>
    /// <param name="date">Date to get range for.</param>
    public static DateRange GetMonthDateRangeStartingWeekday(this DateTime date, DayOfWeek WeekStartDay)
    {
        DateTime firstDayOfMonth = new DateTime(date.Year, date.Month, 1);

        int daysToSubtract = (7 + (firstDayOfMonth.DayOfWeek - WeekStartDay)) % 7;
        DateTime rangeStart = firstDayOfMonth.AddDays(-daysToSubtract);

        DateTime rangeEnd = rangeStart.AddDays(34);

        return new DateRange(rangeStart, rangeEnd);
    }

    public static DateTime GetWeekStartDateForDate(this DateTime date, DayOfWeek firstDayOfWeek)
    {
        // Detect the first day of the week that contains the selected date.
        int diff = (7 + (date.DayOfWeek - firstDayOfWeek)) % 7;

        // Start loading from this date instead of visible date.
        return date.AddDays(-diff).Date;
    }
}
