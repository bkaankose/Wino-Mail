using System;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Extensions
{
    public static class DateTimeExtensions
    {
        /// <summary>
        /// Returns a date range for the month of the given date.
        /// </summary>
        /// <param name="date">Date to get range for.</param>
        public static DateRange GetMonthDateRangeStartingWeekday(this DateTime date, DayOfWeek WeekStartDay)
        {
            var firstDayOfMonth = new DateTime(date.Year, date.Month, 1);

            int daysToWeekDay = (int)firstDayOfMonth.DayOfWeek - (int)WeekStartDay;
            if (daysToWeekDay < 0) daysToWeekDay += 7;

            firstDayOfMonth = firstDayOfMonth.AddDays(-daysToWeekDay);

            var lastDayOfMonth = firstDayOfMonth.AddMonths(1).AddDays(-1);

            return new DateRange(firstDayOfMonth, lastDayOfMonth);
        }

        public static DateTime GetWeekStartDateForDate(this DateTime date, DayOfWeek firstDayOfWeek)
        {
            // Detect the first day of the week that contains the selected date.
            int diff = (7 + (date.DayOfWeek - firstDayOfWeek)) % 7;

            // Start loading from this date instead of visible date.
            return date.AddDays(-diff).Date;
        }
    }
}
