using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Collections
{
    public class CalendarEventCollection
    {
        public event EventHandler<ICalendarItem> CalendarItemAdded;
        public event EventHandler<ICalendarItem> CalendarItemRemoved;

        public event EventHandler<List<ICalendarItem>> CalendarItemRangeAdded;
        public event EventHandler<List<ICalendarItem>> CalendarItemRangeRemoved;

        public event EventHandler CalendarItemsCleared;

        private ObservableRangeCollection<ICalendarItem> _internalRegularEvents = [];
        private ObservableRangeCollection<ICalendarItem> _internalAllDayEvents = [];

        public ReadOnlyObservableCollection<ICalendarItem> RegularEvents { get; }
        public ReadOnlyObservableCollection<ICalendarItem> AllDayEvents { get; }

        public CalendarEventCollection()
        {
            RegularEvents = new ReadOnlyObservableCollection<ICalendarItem>(_internalRegularEvents);
            AllDayEvents = new ReadOnlyObservableCollection<ICalendarItem>(_internalAllDayEvents);
        }

        public bool HasCalendarEvent(AccountCalendar accountCalendar)
        {
            return _internalAllDayEvents.Any(x => x.AssignedCalendar.Id == accountCalendar.Id) ||
                   _internalRegularEvents.Any(x => x.AssignedCalendar.Id == accountCalendar.Id);
        }

        public void AddCalendarItemRange(IEnumerable<ICalendarItem> calendarItems)
        {
            foreach (var calendarItem in calendarItems)
            {
                AddCalendarItem(calendarItem);
            }

            CalendarItemRangeAdded?.Invoke(this, new List<ICalendarItem>(calendarItems));
        }

        public void RemoveCalendarItemRange(IEnumerable<ICalendarItem> calendarItems)
        {
            foreach (var calendarItem in calendarItems)
            {
                RemoveCalendarItem(calendarItem);
            }

            CalendarItemRangeRemoved?.Invoke(this, new List<ICalendarItem>(calendarItems));
        }

        public void AddCalendarItem(ICalendarItem calendarItem)
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

            CalendarItemAdded?.Invoke(this, calendarItem);
        }

        public void Clear()
        {
            _internalAllDayEvents.Clear();
            _internalRegularEvents.Clear();

            CalendarItemsCleared?.Invoke(this, EventArgs.Empty);
        }

        public void RemoveCalendarItem(ICalendarItem calendarItem)
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

            CalendarItemRemoved?.Invoke(this, calendarItem);
        }
    }
}
