using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
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

    /// <summary>
    /// Gets the start date and time in the local time zone for display purposes.
    /// </summary>
    public DateTime LocalStartDateTime => ConvertToLocalTime();

    /// <summary>
    /// Gets the end date and time in the local time zone for display purposes.
    /// </summary>
    public DateTime LocalEndDateTime => ConvertToLocalTime();

    public ITimePeriod Period => CalendarItem.Period;

    public bool IsRecurringEvent => !string.IsNullOrEmpty(CalendarItem.RecurrenceRules) || !string.IsNullOrEmpty(CalendarItem.RecurringEventId);

    [ObservableProperty]
    private bool _isSelected;

    public ObservableCollection<CalendarEventAttendee> Attendees { get; } = new ObservableCollection<CalendarEventAttendee>();

    public CalendarItemType ItemType => CalendarItem.ItemType;

    public CalendarItemViewModel(CalendarItem calendarItem)
    {
        CalendarItem = calendarItem;

        Debug.WriteLine($"{Title} : {ItemType}");
    }

    /// <summary>
    /// Converts a DateTime to local time based on the provided timezone.
    /// If timezone is empty or null, assumes the DateTime is in UTC.
    /// </summary>
    /// <param name="dateTime">The DateTime to convert</param>
    /// <param name="timeZone">The timezone string. If empty/null, assumes UTC.</param>
    /// <returns>DateTime converted to local time</returns>
    private DateTime ConvertToLocalTime()
    {
        // All day events ignore time zones and are treated as local time.
        if (ItemType == CalendarItemType.AllDay || ItemType == CalendarItemType.MultiDayAllDay || ItemType == CalendarItemType.RecurringAllDay)
            return CalendarItem.StartDateTime;

        if (string.IsNullOrEmpty(CalendarItem.TimeZone))
        {
            // If no timezone specified, assume it's UTC and convert to local time
            return DateTime.SpecifyKind(CalendarItem.StartDateTime, DateTimeKind.Utc).ToLocalTime();
        }

        try
        {
            // Parse the timezone and convert to local time
            var sourceTimeZone = TimeZoneInfo.FindSystemTimeZoneById(CalendarItem.TimeZone);
            return TimeZoneInfo.ConvertTimeToUtc(CalendarItem.StartDateTime, sourceTimeZone).ToLocalTime();
        }
        catch (TimeZoneNotFoundException)
        {
            // If timezone is not found, fallback to treating as UTC
            return DateTime.SpecifyKind(CalendarItem.StartDateTime, DateTimeKind.Utc).ToLocalTime();
        }
        catch (InvalidTimeZoneException)
        {
            // If timezone is invalid, fallback to treating as UTC
            return DateTime.SpecifyKind(CalendarItem.StartDateTime, DateTimeKind.Utc).ToLocalTime();
        }
    }

    public override string ToString() => CalendarItem.Title;
}
