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
    }
}
