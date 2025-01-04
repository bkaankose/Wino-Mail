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
        public ITimePeriod Period { get; }
        public CalendarEventCollection EventsCollection { get; }

        public CalendarDayModel(DateTime representingDate, CalendarRenderOptions calendarRenderOptions)
        {
            RepresentingDate = representingDate;
            Period = new TimeRange(representingDate, representingDate.AddDays(1));
            CalendarRenderOptions = calendarRenderOptions;
            EventsCollection = new CalendarEventCollection(Period, calendarRenderOptions.CalendarSettings);
        }

        public DateTime RepresentingDate { get; }
        public CalendarRenderOptions CalendarRenderOptions { get; }
    }
}
