using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Itenso.TimePeriod;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;

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

    /// <summary>
    /// The period of the day where this item is currently being displayed.
    /// Used for multi-day event title formatting.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    public partial ITimePeriod DisplayingPeriod { get; set; }

    /// <summary>
    /// Calendar settings for time formatting.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    public partial CalendarSettings CalendarSettings { get; set; }

    /// <summary>
    /// Gets the display title based on the current displaying period.
    /// </summary>
    public string DisplayTitle
    {
        get
        {
            if (DisplayingPeriod == null || CalendarSettings == null)
                return Title;

            return GetDisplayTitle(DisplayingPeriod, CalendarSettings);
        }
    }

    public ObservableCollection<CalendarEventAttendee> Attendees { get; } = new ObservableCollection<CalendarEventAttendee>();

    public CalendarItemViewModel(CalendarItem calendarItem)
    {
        CalendarItem = calendarItem;
    }

    /// <summary>
    /// Updates the underlying CalendarItem with new data and raises property change notifications.
    /// </summary>
    /// <param name="calendarItem">The updated calendar item data.</param>
    public void UpdateFrom(CalendarItem calendarItem)
    {
        if (calendarItem == null || calendarItem.Id != CalendarItem.Id)
            return;

        // Update all mutable properties
        CalendarItem.Title = calendarItem.Title;
        CalendarItem.Description = calendarItem.Description;
        CalendarItem.Location = calendarItem.Location;
        CalendarItem.StartDate = calendarItem.StartDate;
        CalendarItem.StartTimeZone = calendarItem.StartTimeZone;
        CalendarItem.EndTimeZone = calendarItem.EndTimeZone;
        CalendarItem.DurationInSeconds = calendarItem.DurationInSeconds;
        CalendarItem.Recurrence = calendarItem.Recurrence;
        CalendarItem.RecurringCalendarItemId = calendarItem.RecurringCalendarItemId;
        CalendarItem.OrganizerDisplayName = calendarItem.OrganizerDisplayName;
        CalendarItem.OrganizerEmail = calendarItem.OrganizerEmail;
        CalendarItem.IsLocked = calendarItem.IsLocked;
        CalendarItem.IsHidden = calendarItem.IsHidden;
        CalendarItem.CustomEventColorHex = calendarItem.CustomEventColorHex;
        CalendarItem.HtmlLink = calendarItem.HtmlLink;
        CalendarItem.Status = calendarItem.Status;
        CalendarItem.Visibility = calendarItem.Visibility;
        CalendarItem.ShowAs = calendarItem.ShowAs;
        CalendarItem.UpdatedAt = calendarItem.UpdatedAt;
        CalendarItem.AssignedCalendar = calendarItem.AssignedCalendar;

        // Raise property changed for all bindable properties
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StartDate));
        OnPropertyChanged(nameof(EndDate));
        OnPropertyChanged(nameof(DurationInSeconds));
        OnPropertyChanged(nameof(Period));
        OnPropertyChanged(nameof(IsAllDayEvent));
        OnPropertyChanged(nameof(IsMultiDayEvent));
        OnPropertyChanged(nameof(IsRecurringEvent));
        OnPropertyChanged(nameof(IsRecurringChild));
        OnPropertyChanged(nameof(IsRecurringParent));
        OnPropertyChanged(nameof(AssignedCalendar));
        OnPropertyChanged(nameof(DisplayTitle));
    }

    /// <summary>
    /// Gets the display title for this calendar item when rendered in a specific day.
    /// </summary>
    public string GetDisplayTitle(ITimePeriod displayingPeriod, CalendarSettings calendarSettings)
    {
        if (!IsMultiDayEvent)
            return Title;

        var periodRelation = Period.GetRelation(displayingPeriod);

        if (periodRelation == PeriodRelation.StartInside || periodRelation == PeriodRelation.EnclosingStartTouching)
        {
            // Event starts within this day: "HH:mm -> Title"
            return $"{calendarSettings.GetTimeString(StartDate.TimeOfDay)} -> {Title}";
        }
        else if (periodRelation == PeriodRelation.EndInside || periodRelation == PeriodRelation.EnclosingEndTouching)
        {
            // Event ends within this day: "Title <- HH:mm"
            return $"{Title} <- {calendarSettings.GetTimeString(EndDate.TimeOfDay)}";
        }
        else if (periodRelation == PeriodRelation.Enclosing)
        {
            // Event spans the entire day
            return $"{Translator.CalendarItemAllDay} {Title}";
        }
        else
        {
            return Title;
        }
    }

    public override string ToString() => CalendarItem.Title;
}
