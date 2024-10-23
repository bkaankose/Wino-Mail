using System;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.Models.CalendarTypeStrategies
{
    public class DayCalendarDrawingStrategy : BaseCalendarTypeDrawingStrategy
    {
        public DayCalendarDrawingStrategy(CalendarSettings settings) : base(settings, CalendarDisplayType.Day)
        {

        }

        public override DateRange GetRenderDateRange(DateTime DisplayDate, int DayDisplayCount)
        {
            // Add good amount of days to the left and right of the DisplayDate.

            var start = DisplayDate.AddDays(-4 * DayDisplayCount);
            var end = DisplayDate.AddDays(4 * DayDisplayCount);

            return new DateRange(start, end);
        }

        public override int GetRenderDayCount(DateTime DisplayDate, int DayDisplayCount) => DayDisplayCount;
    }
}
