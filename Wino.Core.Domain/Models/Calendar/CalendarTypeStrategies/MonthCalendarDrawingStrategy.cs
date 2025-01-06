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
            return DateTimeExtensions.GetMonthDateRangeStartingWeekday(CurrentDateRange.EndDate, Settings.FirstDayOfWeek);
        }

        public override DateRange GetPreviousDateRange(DateRange CurrentDateRange, int DayDisplayCount)
        {
            return DateTimeExtensions.GetMonthDateRangeStartingWeekday(CurrentDateRange.StartDate, Settings.FirstDayOfWeek);
        }

        public override DateRange GetRenderDateRange(DateTime DisplayDate, int DayDisplayCount)
        {
            // Load 2 months at first.
            var initialRange = DateTimeExtensions.GetMonthDateRangeStartingWeekday(DisplayDate.Date, Settings.FirstDayOfWeek);

            var nextRange = GetNextDateRange(initialRange, DayDisplayCount);

            return new DateRange(initialRange.StartDate, nextRange.EndDate);
        }

        public override int GetRenderDayCount(DateTime DisplayDate, int DayDisplayCount) => 35;
    }
}
