using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Itenso.TimePeriod;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Interfaces;

namespace Wino.Calendar.ViewModels.Data
{
    public partial class CalendarItemViewModel : ObservableObject, ICalendarItem, ICalendarItemViewModel
    {
        public CalendarItem CalendarItem { get; }

        public string Title => CalendarItem.Title;

        public Guid Id => CalendarItem.Id;

        public IAccountCalendar AssignedCalendar => CalendarItem.AssignedCalendar;

        public DateTime StartDate { get => CalendarItem.StartDate; set => CalendarItem.StartDate = value; }

        public DateTime EndDate => CalendarItem.EndDate;

        public double DurationInSeconds { get => CalendarItem.DurationInSeconds; set => CalendarItem.DurationInSeconds = value; }

        public ITimePeriod Period => CalendarItem.Period;

        public bool IsAllDayEvent => ((ICalendarItem)CalendarItem).IsAllDayEvent;

        public bool IsMultiDayEvent => ((ICalendarItem)CalendarItem).IsMultiDayEvent;

        public bool IsRecurringEvent => !string.IsNullOrEmpty(CalendarItem.Recurrence);

        [ObservableProperty]
        private bool _isSelected;

        public CalendarItemViewModel(CalendarItem calendarItem)
        {
            CalendarItem = calendarItem;
        }

        public override string ToString() => CalendarItem.Title;
    }
}
