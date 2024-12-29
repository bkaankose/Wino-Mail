using System;
using System.Collections.Generic;
using System.Linq;
using Itenso.TimePeriod;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar
{
    /// <summary>
    /// Represents a range of days in the calendar.
    /// Corresponds to 1 view of the FlipView in CalendarPage.
    /// </summary>
    public class DayRangeRenderModel
    {
        public event EventHandler<CalendarDayModel> CalendarDayEventCollectionUpdated;

        public ITimePeriod Period { get; }
        public List<CalendarDayModel> CalendarDays { get; } = [];
        public List<DayHeaderRenderModel> DayHeaders { get; } = [];
        public CalendarRenderOptions CalendarRenderOptions { get; }

        public DayRangeRenderModel(CalendarRenderOptions calendarRenderOptions)
        {
            CalendarRenderOptions = calendarRenderOptions;

            for (var i = 0; i < CalendarRenderOptions.TotalDayCount; i++)
            {
                var representingDate = calendarRenderOptions.DateRange.StartDate.AddDays(i);
                var calendarDayModel = new CalendarDayModel(representingDate, calendarRenderOptions);

                CalendarDays.Add(calendarDayModel);
            }

            Period = new TimeRange(CalendarDays.First().RepresentingDate, CalendarDays.Last().RepresentingDate.AddDays(1));

            // Create day headers based on culture info.

            for (var i = 0; i < 24; i++)
            {
                var representingDate = calendarRenderOptions.DateRange.StartDate.Date.AddHours(i);

                string dayHeader = calendarRenderOptions.CalendarSettings.DayHeaderDisplayType switch
                {
                    DayHeaderDisplayType.TwelveHour => representingDate.ToString("h tt", calendarRenderOptions.CalendarSettings.CultureInfo),
                    DayHeaderDisplayType.TwentyFourHour => representingDate.ToString("HH", calendarRenderOptions.CalendarSettings.CultureInfo),
                    _ => "N/A"
                };

                DayHeaders.Add(new DayHeaderRenderModel(dayHeader, calendarRenderOptions.CalendarSettings.HourHeight));
            }
        }

        //private void RegisterCalendarDayEvents(CalendarDayModel calendarDayModel)
        //{
        //    calendarDayModel.EventsCollection.CalendarItemAdded += CalendarItemAdded;
        //    calendarDayModel.EventsCollection.CalendarItemRangeRemoved += CalendarItemRangeRemoved;
        //    calendarDayModel.EventsCollection.CalendarItemRemoved += CalendarItemRemoved;
        //    calendarDayModel.EventsCollection.CalendarItemRangeAdded += CalendarItemRangeAdded;
        //}

        //private void CalendarItemRangeAdded(object sender, List<ICalendarItem> e)
        //    => CalendarDayEventCollectionUpdated?.Invoke(this, sender as CalendarDayModel);

        //private void CalendarItemRemoved(object sender, ICalendarItem e)
        //    => CalendarDayEventCollectionUpdated?.Invoke(this, sender as CalendarDayModel);

        //private void CalendarItemAdded(object sender, ICalendarItem e)
        //    => CalendarDayEventCollectionUpdated?.Invoke(this, sender as CalendarDayModel);

        //private void CalendarItemRangeRemoved(object sender, List<ICalendarItem> e)
        //    => CalendarDayEventCollectionUpdated?.Invoke(this, sender as CalendarDayModel);

        ///// <summary>
        ///// Unregisters all calendar item change listeners to draw the UI for calendar events.
        ///// </summary>
        //public void UnregisterAll()
        //{
        //    foreach (var day in CalendarDays)
        //    {
        //        day.EventsCollection.CalendarItemRangeRemoved -= CalendarItemRangeRemoved;
        //        day.EventsCollection.CalendarItemRemoved -= CalendarItemRemoved;
        //        day.EventsCollection.CalendarItemRangeAdded -= CalendarItemRangeAdded;
        //        day.EventsCollection.CalendarItemAdded -= CalendarItemAdded;
        //    }
        //}
    }
}
