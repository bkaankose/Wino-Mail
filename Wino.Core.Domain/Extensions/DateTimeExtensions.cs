using System;
using Wino.Core.Domain.Entities.Calendar;
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

    /// <summary>
    /// Converts a datetime from source timezone into local timezone.
    /// If timezone lookup fails, returns original value.
    /// </summary>
    public static DateTime ToLocalTimeFromTimeZone(this DateTime dateTime, string sourceTimeZoneId)
    {
        if (string.IsNullOrWhiteSpace(sourceTimeZoneId))
            return dateTime;

        try
        {
            var sourceTimeZone = TimeZoneInfo.FindSystemTimeZoneById(sourceTimeZoneId);
            var localTimeZone = TimeZoneInfo.Local;
            var unspecifiedDateTime = DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified);

            return TimeZoneInfo.ConvertTime(unspecifiedDateTime, sourceTimeZone, localTimeZone);
        }
        catch
        {
            return dateTime;
        }
    }

    /// <summary>
    /// Converts local datetime into target timezone.
    /// If timezone lookup fails, returns original value.
    /// </summary>
    public static DateTime ToTimeZoneFromLocal(this DateTime localDateTime, string targetTimeZoneId)
    {
        if (string.IsNullOrWhiteSpace(targetTimeZoneId))
            return localDateTime;

        try
        {
            var sourceTimeZone = TimeZoneInfo.Local;
            var targetTimeZone = TimeZoneInfo.FindSystemTimeZoneById(targetTimeZoneId);
            var unspecifiedDateTime = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);

            return TimeZoneInfo.ConvertTime(unspecifiedDateTime, sourceTimeZone, targetTimeZone);
        }
        catch
        {
            return localDateTime;
        }
    }

    public static DateTime GetLocalStartDate(this CalendarItem calendarItem)
        => calendarItem.IsAllDayEvent
            ? calendarItem.StartDate
            : calendarItem.StartDate.ToLocalTimeFromTimeZone(calendarItem.StartTimeZone);

    public static DateTime GetLocalEndDate(this CalendarItem calendarItem)
        => calendarItem.IsAllDayEvent
            ? calendarItem.EndDate
            : calendarItem.EndDate.ToLocalTimeFromTimeZone(calendarItem.EndTimeZone);
}
