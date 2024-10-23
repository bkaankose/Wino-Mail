using System;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.Models.CalendarTypeStrategies
{
    public class WeekCalendarDrawingStrategy : BaseCalendarTypeDrawingStrategy
    {
        public WeekCalendarDrawingStrategy(CalendarSettings settings) : base(settings, Core.Domain.Enums.CalendarDisplayType.Week) { }

        public override DateRange GetRenderDateRange(DateTime DisplayDate, int DayDisplayCount)
        {
            // Detect the first day of the week that contains the selected date.
            DayOfWeek firstDayOfWeek = Settings.FirstDayOfWeek;

            int diff = (7 + (DisplayDate.DayOfWeek - Settings.FirstDayOfWeek)) % 7;

            // Start loading from this date instead of visible date.
            var startDte = DisplayDate.AddDays(-diff).Date;
            var endDte = startDte.AddDays(7);

            return new DateRange(startDte, endDte);
        }

        public override int GetRenderDayCount(DateTime DisplayDate, int DayDisplayCount) => 7;
    }
}
