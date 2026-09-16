using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

public sealed class SyncedCalendarAttendee
{
    public string Name { get; set; }
    public string Email { get; set; }
    public bool IsOptional { get; set; }
    public AttendeeStatus Status { get; set; } = AttendeeStatus.NeedsAction;
}

/// <summary>
/// One calendar occurrence as pulled from an Exchange server, in the provider-neutral shape the change
/// processor maps onto <see cref="Entities.Calendar.CalendarItem"/>. Both the EWS Appointment and the
/// MAPI appointment row (after recurrence expansion) map into this; occurrences are flat, as the EWS
/// path always stored them.
/// </summary>
public sealed class SyncedCalendarEvent
{
    /// <summary>The server's id for the occurrence, already carrying the client tracking id when the server has one.</summary>
    public string RemoteId { get; set; }

    public string Title { get; set; }
    public string Description { get; set; }
    public string Location { get; set; }

    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }

    /// <summary>The IANA zone the event was scheduled in, or null when unknown (the row then stores UTC).</summary>
    public string TimeZoneIana { get; set; }

    public bool IsAllDay { get; set; }

    public string OrganizerEmail { get; set; }
    public string OrganizerName { get; set; }

    public CalendarItemVisibility Visibility { get; set; } = CalendarItemVisibility.Public;
    public CalendarItemShowAs ShowAs { get; set; } = CalendarItemShowAs.Busy;

    /// <summary>The account's own response to a meeting; Accepted for plain appointments.</summary>
    public CalendarItemStatus MyResponse { get; set; } = CalendarItemStatus.Accepted;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<SyncedCalendarAttendee> Attendees { get; set; }

    /// <summary>Popup reminder lead time, or null when no reminder is set.</summary>
    public int? ReminderMinutesBeforeStart { get; set; }

    /// <summary>
    /// For a series master: the rule ("RRULE:..." lines, the app's format). A master is stored so the
    /// app can offer "view/edit series"; it is never rendered itself, its occurrences are.
    /// </summary>
    public string Recurrence { get; set; }

    /// <summary>For an occurrence of a series: the master's <see cref="RemoteId"/>, so the row links to it.</summary>
    public string RecurringParentRemoteId { get; set; }
}
