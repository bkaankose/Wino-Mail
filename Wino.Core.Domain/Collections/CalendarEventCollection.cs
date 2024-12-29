using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Collections
{
    public class CalendarEventCollection
    {
        public event EventHandler<ICalendarItem> CalendarItemAdded;
        public event EventHandler<ICalendarItem> CalendarItemRemoved;

        public event EventHandler<List<ICalendarItem>> CalendarItemRangeAdded;
        public event EventHandler<List<ICalendarItem>> CalendarItemRangeRemoved;

        private ObservableRangeCollection<ICalendarItem> _internalRegularEvents = [];
        private ObservableRangeCollection<ICalendarItem> _internalAllDayEvents = [];

        public ReadOnlyObservableCollection<ICalendarItem> RegularEvents { get; }
        public ReadOnlyObservableCollection<ICalendarItem> AllDayEvents { get; }

        public CalendarEventCollection()
        {
            RegularEvents = new ReadOnlyObservableCollection<ICalendarItem>(_internalRegularEvents);
            AllDayEvents = new ReadOnlyObservableCollection<ICalendarItem>(_internalAllDayEvents);
        }

        public void AddCalendarItemRange(IEnumerable<ICalendarItem> calendarItems, bool reportChange = true)
        {
            foreach (var calendarItem in calendarItems)
            {
                AddCalendarItem(calendarItem, reportChange: false);
            }

            CalendarItemRangeAdded?.Invoke(this, new List<ICalendarItem>(calendarItems));
        }

        public void RemoveCalendarItemRange(IEnumerable<ICalendarItem> calendarItems, bool reportChange = true)
        {
            foreach (var calendarItem in calendarItems)
            {
                RemoveCalendarItem(calendarItem, reportChange);
            }

            CalendarItemRangeRemoved?.Invoke(this, new List<ICalendarItem>(calendarItems));
        }

        public void AddCalendarItem(ICalendarItem calendarItem, bool reportChange = true)
        {
            if (calendarItem is not ICalendarItemViewModel)
                throw new ArgumentException("CalendarItem must be of type ICalendarItemViewModel", nameof(calendarItem));

            if (calendarItem.Period.Duration.TotalMinutes == 1440)
            {
                _internalAllDayEvents.Add(calendarItem);
            }
            else
            {
                _internalRegularEvents.Add(calendarItem);
            }

            if (reportChange)
            {
                CalendarItemAdded?.Invoke(this, calendarItem);
            }
        }

        public void RemoveCalendarItem(ICalendarItem calendarItem, bool reportChange = true)
        {
            if (calendarItem is not ICalendarItemViewModel)
                throw new ArgumentException("CalendarItem must be of type ICalendarItemViewModel", nameof(calendarItem));

            if (calendarItem.Period.Duration.TotalMinutes == 1440)
            {
                _internalAllDayEvents.Remove(calendarItem);
            }
            else
            {
                _internalRegularEvents.Remove(calendarItem);
            }

            if (reportChange)
            {
                CalendarItemRemoved?.Invoke(this, calendarItem);
            }
        }
    }
}
