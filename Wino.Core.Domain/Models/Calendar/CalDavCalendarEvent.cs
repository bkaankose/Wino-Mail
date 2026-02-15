using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

public sealed class CalDavCalendarEvent
{
    public string RemoteEventId { get; init; } = string.Empty;
    public string RemoteResourceHref { get; init; } = string.Empty;
    public string ETag { get; init; } = string.Empty;
    public string IcsContent { get; init; } = string.Empty;

    public string Uid { get; init; } = string.Empty;
    public string SeriesMasterRemoteEventId { get; init; } = string.Empty;
    public bool IsSeriesMaster { get; init; }
    public bool IsRecurringInstance { get; init; }

    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;

    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public string StartTimeZone { get; init; } = string.Empty;
    public string EndTimeZone { get; init; } = string.Empty;
    public string Recurrence { get; init; } = string.Empty;

    public string OrganizerDisplayName { get; init; } = string.Empty;
    public string OrganizerEmail { get; init; } = string.Empty;

    public CalendarItemStatus Status { get; init; } = CalendarItemStatus.Accepted;
    public CalendarItemVisibility Visibility { get; init; } = CalendarItemVisibility.Default;
    public CalendarItemShowAs ShowAs { get; init; } = CalendarItemShowAs.Busy;
    public bool IsHidden { get; init; }

    public IReadOnlyList<CalDavEventAttendee> Attendees { get; init; } = [];
    public IReadOnlyList<CalDavEventReminder> Reminders { get; init; } = [];
}

public sealed class CalDavEventAttendee
{
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public AttendeeStatus AttendenceStatus { get; init; } = AttendeeStatus.NeedsAction;
    public bool IsOrganizer { get; init; }
    public bool IsOptionalAttendee { get; init; }
}

public sealed class CalDavEventReminder
{
    public int DurationInSeconds { get; init; }
    public CalendarItemReminderType ReminderType { get; init; } = CalendarItemReminderType.Popup;
}

