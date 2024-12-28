using System.Collections.ObjectModel;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Collections
{
    public class CalendarEventCollection : ObservableRangeCollection<ICalendarItem>
    {
        public ObservableCollection<ICalendarItem> AllDayEvents { get; } = new ObservableCollection<ICalendarItem>();
        public new void Add(ICalendarItem calendarItem)
        {
            base.Add(calendarItem);

            if (calendarItem.Period.Duration.TotalMinutes == 1440)
            {
                AllDayEvents.Add(calendarItem);
            }
        }

        public new void Remove(ICalendarItem calendarItem)
        {
            base.Remove(calendarItem);

            if (calendarItem.Period.Duration.TotalMinutes == 1440)
            {
                AllDayEvents.Remove(calendarItem);
            }
        }
    }
}
