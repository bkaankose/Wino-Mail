using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Itenso.TimePeriod;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Collections
{
    public class CalendarEventCollection
    {
        public event EventHandler<ICalendarItem> CalendarItemAdded;
        public event EventHandler<ICalendarItem> CalendarItemRemoved;

        public event EventHandler CalendarItemsCleared;

        private ObservableRangeCollection<ICalendarItem> _internalRegularEvents = [];
        private ObservableRangeCollection<ICalendarItem> _internalAllDayEvents = [];

        public ReadOnlyObservableCollection<ICalendarItem> RegularEvents { get; }
        public ReadOnlyObservableCollection<ICalendarItem> AllDayEvents { get; }
        public ITimePeriod Period { get; }

        private readonly List<ICalendarItem> _allItems = new List<ICalendarItem>();

        public CalendarEventCollection(ITimePeriod period)
        {
            Period = period;

            RegularEvents = new ReadOnlyObservableCollection<ICalendarItem>(_internalRegularEvents);
            AllDayEvents = new ReadOnlyObservableCollection<ICalendarItem>(_internalAllDayEvents);
        }

        public bool HasCalendarEvent(AccountCalendar accountCalendar)
            => _allItems.Any(x => x.AssignedCalendar.Id == accountCalendar.Id);

        public void FilterByCalendars(IEnumerable<Guid> visibleCalendarIds)
        {
            foreach (var item in _allItems)
            {
                var collection = GetProperCollectionForCalendarItem(item);

                if (!visibleCalendarIds.Contains(item.AssignedCalendar.Id) && collection.Contains(item))
                {
                    RemoveCalendarItemInternal(collection, item, false);
                }
                else if (visibleCalendarIds.Contains(item.AssignedCalendar.Id) && !collection.Contains(item))
                {
                    AddCalendarItemInternal(collection, item, false);
                }
            }
        }

        private ObservableRangeCollection<ICalendarItem> GetProperCollectionForCalendarItem(ICalendarItem calendarItem)
        {
            // Event duration is not simply enough to determine whether it's an all-day event or not.
            // Event may start at 11:00 PM and end next day at 11:00 PM. It's not an all-day event.
            // It's a multi-day event.

            bool isAllDayEvent = calendarItem.Period.Duration.TotalDays == 1 && calendarItem.Period.Start.TimeOfDay == TimeSpan.Zero;

            return isAllDayEvent ? _internalAllDayEvents : _internalRegularEvents;
        }

        public void AddCalendarItem(ICalendarItem calendarItem)
        {
            var collection = GetProperCollectionForCalendarItem(calendarItem);
            AddCalendarItemInternal(collection, calendarItem);
        }

        public void RemoveCalendarItem(ICalendarItem calendarItem)
        {
            var collection = GetProperCollectionForCalendarItem(calendarItem);
            RemoveCalendarItemInternal(collection, calendarItem);
        }

        private void AddCalendarItemInternal(ObservableRangeCollection<ICalendarItem> collection, ICalendarItem calendarItem, bool create = true)
        {
            if (calendarItem is not ICalendarItemViewModel)
                throw new ArgumentException("CalendarItem must be of type ICalendarItemViewModel", nameof(calendarItem));

            collection.Add(calendarItem);

            if (create)
            {
                _allItems.Add(calendarItem);
            }

            CalendarItemAdded?.Invoke(this, calendarItem);
        }

        private void RemoveCalendarItemInternal(ObservableRangeCollection<ICalendarItem> collection, ICalendarItem calendarItem, bool destroy = true)
        {
            if (calendarItem is not ICalendarItemViewModel)
                throw new ArgumentException("CalendarItem must be of type ICalendarItemViewModel", nameof(calendarItem));

            collection.Remove(calendarItem);

            if (destroy)
            {
                _allItems.Remove(calendarItem);
            }

            CalendarItemRemoved?.Invoke(this, calendarItem);
        }

        public void Clear()
        {
            _internalAllDayEvents.Clear();
            _internalRegularEvents.Clear();
            _allItems.Clear();

            CalendarItemsCleared?.Invoke(this, EventArgs.Empty);
        }
    }
}
