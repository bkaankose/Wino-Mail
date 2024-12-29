using System;
using System.Collections.ObjectModel;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Collections
{
    // TODO: Could be read-only collection in the MVVM package.
    public class CalendarEventCollection : ObservableRangeCollection<ICalendarItem>
    {
        public ObservableCollection<ICalendarItem> AllDayEvents { get; } = new ObservableCollection<ICalendarItem>();
        public new void Add(ICalendarItem calendarItem)
        {
            if (calendarItem is not ICalendarItemViewModel)
                throw new ArgumentException("CalendarItem must be of type ICalendarItemViewModel", nameof(calendarItem));

            base.Add(calendarItem);

            if (calendarItem.Period.Duration.TotalMinutes == 1440)
            {
                AllDayEvents.Add(calendarItem);
            }
        }

        public new void Remove(ICalendarItem calendarItem)
        {
            if (calendarItem is not ICalendarItemViewModel)
                throw new ArgumentException("CalendarItem must be of type ICalendarItemViewModel", nameof(calendarItem));

            base.Remove(calendarItem);

            if (calendarItem.Period.Duration.TotalMinutes == 1440)
            {
                AllDayEvents.Remove(calendarItem);
            }
        }
    }
}
