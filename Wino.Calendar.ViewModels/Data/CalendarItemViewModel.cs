using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Itenso.TimePeriod;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Interfaces;

namespace Wino.Calendar.ViewModels.Data;

public partial class CalendarItemViewModel : ObservableObject, ICalendarItem, ICalendarItemViewModel
{
    public CalendarItem CalendarItem { get; }

    public string Title => CalendarItem.Title;

    public Guid Id => CalendarItem.Id;

    public IAccountCalendar AssignedCalendar => CalendarItem.AssignedCalendar;

    /// <summary>
    /// Gets or sets the start date converted to user's local timezone for display.
    /// The underlying CalendarItem stores dates according to their timezone.
    /// </summary>
    public DateTime StartDate
    {
        get
        {
            // Get start date in user's local timezone
            return CalendarItem.LocalStartDate;
        }
        set
        {
            // When setting from UI (in local time), convert to event's timezone for storage
            if (!string.IsNullOrEmpty(CalendarItem.StartTimeZone))
            {
                try
                {
                    var sourceTimeZone = TimeZoneInfo.Local;
                    var targetTimeZone = TimeZoneInfo.FindSystemTimeZoneById(CalendarItem.StartTimeZone);
                    CalendarItem.StartDate = TimeZoneInfo.ConvertTime(value, sourceTimeZone, targetTimeZone);
                }
                catch
                {
                    // If timezone lookup fails, set as-is
                    CalendarItem.StartDate = value;
                }
            }
            else
            {
                // No timezone info, set as-is
                CalendarItem.StartDate = value;
            }
        }
    }

    /// <summary>
    /// Gets the end date converted to user's local timezone for display.
    /// The underlying CalendarItem stores dates according to their timezone.
    /// </summary>
    public DateTime EndDate
    {
        get
        {
            // Get end date in user's local timezone
            return CalendarItem.LocalEndDate;
        }
    }

    public double DurationInSeconds { get => CalendarItem.DurationInSeconds; set => CalendarItem.DurationInSeconds = value; }

    /// <summary>
    /// Gets the time period in local time.
    /// </summary>
    public ITimePeriod Period
    {
        get
        {
            // Return a period using local times for UI display
            return new TimeRange(StartDate, EndDate);
        }
    }

    public bool IsAllDayEvent => CalendarItem.IsAllDayEvent;
    public bool IsMultiDayEvent => CalendarItem.IsMultiDayEvent;
    public bool IsRecurringEvent => CalendarItem.IsRecurringEvent;
    public bool IsRecurringChild => CalendarItem.IsRecurringChild;
    public bool IsRecurringParent => CalendarItem.IsRecurringParent;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public ObservableCollection<CalendarEventAttendee> Attendees { get; } = new ObservableCollection<CalendarEventAttendee>();

    public CalendarItemViewModel(CalendarItem calendarItem)
    {
        CalendarItem = calendarItem;
    }

    public override string ToString() => CalendarItem.Title;
}
