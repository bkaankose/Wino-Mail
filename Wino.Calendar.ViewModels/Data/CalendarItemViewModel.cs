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
    /// Gets or sets the start date in local time based on the event's timezone.
    /// The underlying CalendarItem stores dates in UTC.
    /// </summary>
    public DateTime StartDate
    {
        get
        {
            // Convert from UTC stored in database to local time using the event's timezone
            var startDateTimeOffset = CalendarItem.StartDateTimeOffset;
            return startDateTimeOffset.LocalDateTime;
        }
        set
        {
            // When setting, convert from local time to UTC for storage
            // Preserve the timezone information
            if (!string.IsNullOrEmpty(CalendarItem.StartTimeZone))
            {
                try
                {
                    var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(CalendarItem.StartTimeZone);
                    var utcDateTime = TimeZoneInfo.ConvertTimeToUtc(value, timeZoneInfo);
                    CalendarItem.StartDate = utcDateTime;
                }
                catch
                {
                    // If timezone lookup fails, assume value is already in UTC
                    CalendarItem.StartDate = value;
                }
            }
            else
            {
                // No timezone info, assume UTC
                CalendarItem.StartDate = value;
            }
        }
    }

    /// <summary>
    /// Gets the end date in local time based on the event's timezone.
    /// The underlying CalendarItem stores dates in UTC.
    /// </summary>
    public DateTime EndDate
    {
        get
        {
            // Convert from UTC stored in database to local time using the event's timezone
            var endDateTimeOffset = CalendarItem.EndDateTimeOffset;
            return endDateTimeOffset.LocalDateTime;
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