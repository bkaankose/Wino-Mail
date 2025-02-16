using System.Linq;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Domain.Collections
{
    public class DayRangeCollection : ObservableRangeCollection<DayRangeRenderModel>
    {
        /// <summary>
        /// Gets the range of dates that are currently displayed in the collection.
        /// </summary>
        public DateRange DisplayRange
        {
            get
            {
                if (Count == 0) return null;

                var minimumLoadedDate = this[0].CalendarRenderOptions.DateRange.StartDate;
                var maximumLoadedDate = this[Count - 1].CalendarRenderOptions.DateRange.EndDate;

                return new DateRange(minimumLoadedDate, maximumLoadedDate);
            }
        }

        public void RemoveCalendarItem(ICalendarItem calendarItem)
        {
            foreach (var dayRange in this)
            {

            }
        }

        public void AddCalendarItem(ICalendarItem calendarItem)
        {
            foreach (var dayRange in this)
            {
                var calendarDayModel = dayRange.CalendarDays.FirstOrDefault(x => x.Period.HasInside(calendarItem.Period.Start));
                calendarDayModel?.EventsCollection.AddCalendarItem(calendarItem);
            }
        }
    }
}
