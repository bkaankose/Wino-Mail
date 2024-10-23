using System;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.Models.CalendarTypeStrategies
{
    public abstract class BaseCalendarTypeDrawingStrategy
    {
        public CalendarSettings Settings { get; }
        public CalendarDisplayType HandlingType { get; }

        /// <summary>
        /// Day range of the pre-rendered items.
        /// </summary>
        public abstract DateRange GetRenderDateRange(DateTime DisplayDate, int DayDisplayCount);

        /// <summary>
        /// How many items should be placed in 1 FlipViewItem.
        /// </summary>
        public abstract int GetRenderDayCount(DateTime DisplayDate, int DayDisplayCount);

        protected BaseCalendarTypeDrawingStrategy(CalendarSettings settings, CalendarDisplayType handlingType)
        {
            Settings = settings;
            HandlingType = handlingType;
        }
    }
}
