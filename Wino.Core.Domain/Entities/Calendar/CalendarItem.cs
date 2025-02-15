﻿using System;
using System.Diagnostics;
using Itenso.TimePeriod;
using SQLite;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

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

    public DateTime StartDate { get; set; }

    public DateTime EndDate
    {
        get
        {
            return StartDate.AddSeconds(DurationInSeconds);
        }
    }

    public TimeSpan StartDateOffset { get; set; }
    public TimeSpan EndDateOffset { get; set; }

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
    /// Events that are either an exceptional instance of a recurring event or occurrences.
    /// IsOccurrence is used to display occurrence instances of parent recurring events.
    /// IsOccurrence == false && IsRecurringChild == true => exceptional single instance.
    /// </summary>
    public bool IsRecurringChild
    {
        get
        {
            return RecurringCalendarItemId != null;
        }
    }

    /// <summary>
    /// Events that are either an exceptional instance of a recurring event or occurrences.
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
    public CalendarItemStatus Status { get; set; }
    public CalendarItemVisibility Visibility { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid CalendarId { get; set; }

    [Ignore]
    public IAccountCalendar AssignedCalendar { get; set; }

    /// <summary>
    /// Whether this item does not really exist in the database or not.
    /// These are used to display occurrence instances of parent recurring events.
    /// </summary>
    [Ignore]
    public bool IsOccurrence { get; set; }

    /// <summary>
    /// Id to load information related to this event.
    /// Occurrences tracked by the parent recurring event if they are not exceptional instances.
    /// Recurring children here are exceptional instances. They have their own info in the database including Id.
    /// </summary>
    public Guid EventTrackingId => IsOccurrence ? RecurringCalendarItemId.Value : Id;

    public CalendarItem CreateRecurrence(DateTime startDate, double durationInSeconds)
    {
        // Create a copy with the new start date and duration

        return new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = Title,
            Description = Description,
            Location = Location,
            StartDate = startDate,
            DurationInSeconds = durationInSeconds,
            Recurrence = Recurrence,
            OrganizerDisplayName = OrganizerDisplayName,
            OrganizerEmail = OrganizerEmail,
            RecurringCalendarItemId = Id,
            AssignedCalendar = AssignedCalendar,
            CalendarId = CalendarId,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Visibility = Visibility,
            Status = Status,
            CustomEventColorHex = CustomEventColorHex,
            HtmlLink = HtmlLink,
            StartDateOffset = StartDateOffset,
            EndDateOffset = EndDateOffset,
            RemoteEventId = RemoteEventId,
            IsHidden = IsHidden,
            IsLocked = IsLocked,
            IsOccurrence = true
        };
    }
}
