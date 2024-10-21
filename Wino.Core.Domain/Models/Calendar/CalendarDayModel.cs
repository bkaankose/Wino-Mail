using System;
using System.Collections.ObjectModel;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Calendar
{
    /// <summary>
    /// Represents a day in the calendar.
    /// Can hold events, appointments, wheather status etc.
    /// </summary>
    public class CalendarDayModel
    {
        public ObservableCollection<ICalendarItem> Events { get; } = new ObservableCollection<ICalendarItem>();
        public CalendarDayModel(DateTime representingDate, CalendarRenderOptions calendarRenderOptions)
        {
            RepresentingDate = representingDate;
            CalendarRenderOptions = calendarRenderOptions;
        }

        public DateTime RepresentingDate { get; }
        public CalendarRenderOptions CalendarRenderOptions { get; }
    }
}
