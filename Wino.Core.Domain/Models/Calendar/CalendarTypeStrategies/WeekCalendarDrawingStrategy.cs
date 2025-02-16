using System;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Domain.Models.Calendar.CalendarTypeStrategies
{
    public class WeekCalendarDrawingStrategy : BaseCalendarTypeDrawingStrategy
    {
        public WeekCalendarDrawingStrategy(CalendarSettings settings) : base(settings, Enums.CalendarDisplayType.Week) { }

        public override DateRange GetNextDateRange(DateRange CurrentDateRange, int DayDisplayCount)
            => new DateRange(CurrentDateRange.EndDate, CurrentDateRange.EndDate.AddDays(7 * 2));

        public override DateRange GetPreviousDateRange(DateRange CurrentDateRange, int DayDisplayCount)
            => new DateRange(CurrentDateRange.StartDate.AddDays(-7 * 2), CurrentDateRange.StartDate);

        public override DateRange GetRenderDateRange(DateTime DisplayDate, int DayDisplayCount)
        {
            // Detect the first day of the week that contains the selected date.
            DayOfWeek firstDayOfWeek = Settings.FirstDayOfWeek;

            int diff = (7 + (DisplayDate.DayOfWeek - Settings.FirstDayOfWeek)) % 7;

            // Start loading from this date instead of visible date.
            var weekStartDate = DisplayDate.AddDays(-diff).Date;

            // Load -+ 14 days
            var startDate = weekStartDate.AddDays(-14);
            var endDte = weekStartDate.AddDays(14);

            return new DateRange(startDate, endDte);
        }

        public override int GetRenderDayCount(DateTime DisplayDate, int DayDisplayCount) => 7;
    }
}
