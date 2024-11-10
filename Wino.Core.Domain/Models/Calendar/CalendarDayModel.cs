using System;
using System.Collections.ObjectModel;
using Itenso.TimePeriod;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Calendar
{
    /// <summary>
    /// Represents a day in the calendar.
    /// Can hold events, appointments, wheather status etc.
    /// </summary>
    public class CalendarDayModel
    {
        public TimeRange Period { get; }
        public ObservableCollection<ICalendarItem> Events { get; } = new ObservableCollection<ICalendarItem>();
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
