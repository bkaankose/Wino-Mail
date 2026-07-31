using System;
using System.Diagnostics;
using Itenso.TimePeriod;
using SQLite;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Domain.Entities.Calendar;

[DebuggerDisplay("{Title} ({StartDate} - {EndDate})")]
public class CalendarItem : ICalendarItem
{
    [PrimaryKey]
    public Guid Id { get; set; }
    public string RemoteEventId { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public string Location { get; set; }

    /// <summary>
    /// Indicates whether this item is a local preview that hasn't been synced to the server yet.
    /// When true, the item exists only in the local database without a RemoteEventId.
    /// Used to prevent duplicates when the server returns the newly created event.
    /// </summary>
    [Ignore]
    public bool IsLocalPreview => string.IsNullOrEmpty(RemoteEventId);

    public DateTime StartDate { get; set; }

    public DateTime EndDate
    {
        get
        {
            return StartDate.AddSeconds(DurationInSeconds);
        }
    }

    /// <summary>
    /// IANA timezone identifier for the start time (e.g., "America/New_York", "Europe/London").
    /// If null or empty, UTC is assumed.
    /// </summary>
    public string StartTimeZone { get; set; }

    /// <summary>
    /// IANA timezone identifier for the end time (e.g., "America/New_York", "Europe/London").
    /// If null or empty, UTC is assumed.
    /// </summary>
    public string EndTimeZone { get; set; }

    private ITimePeriod _period;
    public ITimePeriod Period
    {
        get
        {
            _period ??= new TimeRange(StartDate, EndDate);

            return _period;
        }
    }

    /// <summary>
    /// Events that starts at midnight and ends at midnight are considered all-day events.
    /// </summary>
    public bool IsAllDayEvent
    {
        get
        {
            return
                StartDate.TimeOfDay == TimeSpan.Zero &&
                EndDate.TimeOfDay == TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Events that are child instances of a recurring event (occurrences or exceptions).
    /// </summary>
    public bool IsRecurringChild
    {
        get
        {
            return RecurringCalendarItemId != null;
        }
    }

    /// <summary>
    /// Events that are part of a recurring series (either as parent or child).
    /// </summary>
    public bool IsRecurringEvent => IsRecurringChild || IsRecurringParent;

    /// <summary>
    /// Events that are the master event definition of recurrence events.
    /// </summary>
    public bool IsRecurringParent
    {
        get
        {
            return !string.IsNullOrEmpty(Recurrence) && RecurringCalendarItemId == null;
        }
    }

    /// <summary>
    /// Events that are not all-day events and last more than one day are considered multi-day events.
    /// </summary>
    public bool IsMultiDayEvent
    {
        get
        {
            return Period.Duration.TotalDays >= 1 && !IsAllDayEvent;
        }
    }

    public double DurationInSeconds { get; set; }
    public string Recurrence { get; set; }

    public string OrganizerDisplayName { get; set; }
    public string OrganizerEmail { get; set; }

    /// <summary>
    /// The id of the parent calendar item of the recurring event.
    /// Exceptional instances are stored as a separate calendar item.
    /// This makes the calendar item a child of the recurring event.
    /// </summary>
    public Guid? RecurringCalendarItemId { get; set; }

    /// <summary>
    /// Indicates read-only events. Default is false.
    /// </summary>
    public bool IsLocked { get; set; }

    /// <summary>
    /// Hidden events must not be displayed to the user.
    /// This usually happens when a child instance of recurring parent is cancelled after creation.
    /// </summary>
    public bool IsHidden { get; set; }

    // TODO
    public string CustomEventColorHex { get; set; }
    public string HtmlLink { get; set; }
    public DateTime? SnoozedUntil { get; set; }
    public CalendarItemStatus Status { get; set; }
    public CalendarItemVisibility Visibility { get; set; }

    /// <summary>
    /// Indicates how the event should be shown in the calendar (Free, Busy, Tentative, etc.).
    /// </summary>
    public CalendarItemShowAs ShowAs { get; set; } = CalendarItemShowAs.Busy;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid CalendarId { get; set; }

    [Ignore]
    public IAccountCalendar AssignedCalendar { get; set; }

    [Ignore]
    public bool CanChangeStartAndEndDate
    {
        get
        {
            if (IsLocked)
            {
                return false;
            }

            var accountAddress = AssignedCalendar?.MailAccount?.Address;

            return string.IsNullOrWhiteSpace(OrganizerEmail) ||
                   string.IsNullOrWhiteSpace(accountAddress) ||
                   string.Equals(OrganizerEmail, accountAddress, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Id to load information related to this event (attendees, reminders, etc.).
    /// For child events, if they have their own data, use their own Id.
    /// For events that share data with their parent, return parent's Id.
    /// </summary>
    public Guid EventTrackingId => Id;

    /// <summary>
    /// Gets the start date converted to user's local timezone for display.
    /// StartDate is stored according to StartTimeZone.
    /// </summary>
    [Ignore]
    public DateTime LocalStartDate
    {
        get
        {
            return this.GetLocalStartDate();
        }
    }

    /// <summary>
    /// Gets the end date converted to user's local timezone for display.
    /// EndDate is calculated from StartDate and is in StartTimeZone.
    /// </summary>
    [Ignore]
    public DateTime LocalEndDate
    {
        get
        {
            return this.GetLocalEndDate();
        }
    }

    public string GetDisplayTitle(ITimePeriod displayingPeriod, CalendarSettings calendarSettings) => Period.ToString();
}
