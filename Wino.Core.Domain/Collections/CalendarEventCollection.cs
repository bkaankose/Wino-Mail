using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Itenso.TimePeriod;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Domain.Collections;

public class CalendarEventCollection
{
    public event EventHandler<ICalendarItem> CalendarItemAdded;
    public event EventHandler<ICalendarItem> CalendarItemRemoved;

    public event EventHandler CalendarItemsCleared;

    private ObservableRangeCollection<ICalendarItem> _internalRegularEvents = [];
    private ObservableRangeCollection<ICalendarItem> _internalAllDayEvents = [];

    public ReadOnlyObservableCollection<ICalendarItem> RegularEvents { get; }
    public ReadOnlyObservableCollection<ICalendarItem> AllDayEvents { get; } // TODO: Rename this to include multi-day events.
    public ITimePeriod Period { get; }
    public CalendarSettings Settings { get; }

    private readonly List<ICalendarItem> _allItems = new List<ICalendarItem>();

    public CalendarEventCollection(ITimePeriod period, CalendarSettings settings)
    {
        Period = period;
        Settings = settings;

        RegularEvents = new ReadOnlyObservableCollection<ICalendarItem>(_internalRegularEvents);
        AllDayEvents = new ReadOnlyObservableCollection<ICalendarItem>(_internalAllDayEvents);
    }

    public bool HasCalendarEvent(AccountCalendar accountCalendar)
        => _allItems.Any(x => x.AssignedCalendar.Id == accountCalendar.Id);

    public ICalendarItem GetCalendarItem(Guid calendarItemId)
    {
        return _allItems.FirstOrDefault(x => x.Id == calendarItemId);
    }

    public void ClearSelectionStates()
    {
        foreach (var item in _allItems)
        {
            if (item is ICalendarItemViewModel calendarItemViewModel)
            {
                calendarItemViewModel.IsSelected = false;
            }
        }
    }

    public void FilterByCalendars(IEnumerable<Guid> visibleCalendarIds)
    {
        foreach (var item in _allItems)
        {
            var collections = GetProperCollectionsForCalendarItem(item);

            foreach (var collection in collections)
            {
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
    }

    private IEnumerable<ObservableRangeCollection<ICalendarItem>> GetProperCollectionsForCalendarItem(ICalendarItem calendarItem)
    {
        // All-day events go to all days.
        // Multi-day events go to both.
        // Anything else goes to regular.

        if (calendarItem.IsAllDayEvent)
        {
            return [_internalAllDayEvents];
        }
        else if (calendarItem.IsMultiDayEvent)
        {
            return [_internalRegularEvents, _internalAllDayEvents];
        }
        else
        {
            return [_internalRegularEvents];
        }
    }

    public void AddCalendarItem(ICalendarItem calendarItem)
    {
        var collections = GetProperCollectionsForCalendarItem(calendarItem);

        foreach (var collection in collections)
        {
            AddCalendarItemInternal(collection, calendarItem);
        }
    }

    public void RemoveCalendarItem(ICalendarItem calendarItem)
    {
        var collections = GetProperCollectionsForCalendarItem(calendarItem);

        foreach (var collection in collections)
        {
            RemoveCalendarItemInternal(collection, calendarItem);
        }
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
