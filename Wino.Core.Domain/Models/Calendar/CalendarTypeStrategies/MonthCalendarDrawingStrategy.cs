using System;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;

namespace Wino.Core.Domain.Models.Calendar.CalendarTypeStrategies
{
    public class MonthCalendarDrawingStrategy : BaseCalendarTypeDrawingStrategy
    {
        public MonthCalendarDrawingStrategy(CalendarSettings settings)
            : base(settings, CalendarDisplayType.Month)
        {
        }

        public override DateRange GetNextDateRange(DateRange CurrentDateRange, int DayDisplayCount)
        {
            return new DateRange(CurrentDateRange.EndDate, CurrentDateRange.EndDate.AddDays(35));
        }

        public override DateRange GetPreviousDateRange(DateRange CurrentDateRange, int DayDisplayCount)
        {
            return new DateRange(CurrentDateRange.StartDate.AddDays(-35), CurrentDateRange.StartDate);
        }

        public override DateRange GetRenderDateRange(DateTime DisplayDate, int DayDisplayCount)
        {
            // Get the first day of the month.
            var firstDayOfMonth = new DateTime(DisplayDate.Year, DisplayDate.Month, 1);
            return DateTimeExtensions.GetMonthDateRangeStartingWeekday(firstDayOfMonth, Settings.FirstDayOfWeek);
        }

        public override int GetRenderDayCount(DateTime DisplayDate, int DayDisplayCount) => 35;
    }
}
