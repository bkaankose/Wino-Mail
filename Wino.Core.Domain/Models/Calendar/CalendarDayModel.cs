using System;
using Itenso.TimePeriod;
using Wino.Core.Domain.Collections;

namespace Wino.Core.Domain.Models.Calendar
{
    /// <summary>
    /// Represents a day in the calendar.
    /// Can hold events, appointments, wheather status etc.
    /// </summary>
    public class CalendarDayModel
    {
        public TimeRange Period { get; }
        public CalendarEventCollection EventsCollection { get; } = new CalendarEventCollection();
        public CalendarDayModel(DateTime representingDate, CalendarRenderOptions calendarRenderOptions)
        {
            RepresentingDate = representingDate;
            Period = new TimeRange(representingDate, representingDate.AddDays(1));
            CalendarRenderOptions = calendarRenderOptions;
        }

        public DateTime RepresentingDate { get; }
        public CalendarRenderOptions CalendarRenderOptions { get; }
    }
}
