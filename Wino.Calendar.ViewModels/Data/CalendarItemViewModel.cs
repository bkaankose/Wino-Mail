using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Itenso.TimePeriod;
using Wino.Core.Domain.Interfaces;

namespace Wino.Calendar.ViewModels.Data
{
    public partial class CalendarItemViewModel : ObservableObject, ICalendarItem
    {
        public ICalendarItem CalendarItem { get; }

        public string Title => CalendarItem.Title;

        public Guid Id => CalendarItem.Id;

        public DateTimeOffset StartTime => CalendarItem.StartTime;

        public int DurationInMinutes => CalendarItem.DurationInMinutes;

        public TimeRange Period => CalendarItem.Period;

        public CalendarItemViewModel(ICalendarItem calendarItem)
        {
            CalendarItem = calendarItem;
        }
    }
}
