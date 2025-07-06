using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Itenso.TimePeriod;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Calendar.ViewModels.Data;

public partial class CalendarItemViewModel : ObservableObject, ICalendarItem, ICalendarItemViewModel
{
    public CalendarItem CalendarItem { get; }

    public string Title => CalendarItem.Title;

    public Guid Id => CalendarItem.Id;

    public IAccountCalendar AssignedCalendar => CalendarItem.AssignedCalendar;

    public DateTime StartDateTime { get => CalendarItem.StartDateTime; set => CalendarItem.StartDateTime = value; }

    public DateTime EndDateTime => CalendarItem.EndDateTime;

    public ITimePeriod Period => CalendarItem.Period;

    public bool IsRecurringEvent => !string.IsNullOrEmpty(CalendarItem.RecurrenceRules) || !string.IsNullOrEmpty(CalendarItem.RecurringEventId);

    [ObservableProperty]
    private bool _isSelected;

    public ObservableCollection<CalendarEventAttendee> Attendees { get; } = new ObservableCollection<CalendarEventAttendee>();

    public CalendarItemType ItemType => ((ICalendarItem)CalendarItem).ItemType;

    public CalendarItemViewModel(CalendarItem calendarItem)
    {
        CalendarItem = calendarItem;
    }

    public override string ToString() => CalendarItem.Title;
}
