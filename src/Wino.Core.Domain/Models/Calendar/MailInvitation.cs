#nullable enable
using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

public enum MailInvitationMethod
{
    Request,
    Cancel,
    Reply,
    Other
}

public sealed class MailInvitationAttendee
{
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public AttendeeStatus Status { get; init; }
    public bool IsOptional { get; init; }
}

/// <summary>
/// The first VEVENT of an iMIP message (RFC 6047), read from the message's text/calendar part.
/// </summary>
public sealed class MailInvitation
{
    public MailInvitationMethod Method { get; init; }
    public string Uid { get; init; } = string.Empty;
    public int Sequence { get; init; }
    public string RecurrenceId { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public bool IsAllDay { get; init; }
    public bool IsRecurring { get; init; }
    public string OrganizerName { get; init; } = string.Empty;
    public string OrganizerEmail { get; init; } = string.Empty;
    public List<MailInvitationAttendee> Attendees { get; init; } = [];
    public string RawIcs { get; init; } = string.Empty;
}

/// <summary>
/// What the reader shows for an invitation mail: the invitation and how it relates to the
/// account's calendar.
/// </summary>
public sealed class MailInvitationState
{
    public required MailInvitation Invitation { get; init; }

    /// <summary>The user's own answer, from the stored event or the invitation; null when the user is not an attendee yet (forwarded invitation).</summary>
    public AttendeeStatus? CurrentResponse { get; init; }

    public Guid? CalendarItemId { get; init; }
    public bool IsInCalendar => CalendarItemId.HasValue;

    /// <summary>The user organizes this event; the card shows details only.</summary>
    public bool IsOrganizer { get; init; }

    /// <summary>The stored event has a newer SEQUENCE than this message, so this message is out of date.</summary>
    public bool IsOutdated { get; init; }

    /// <summary>This message has a newer SEQUENCE than the stored event; the user's earlier answer no longer applies.</summary>
    public bool IsUpdate { get; init; }

    /// <summary>The organizer cancelled the event (METHOD:CANCEL or STATUS:CANCELLED).</summary>
    public bool IsCancelled { get; init; }

    public bool CanRespond { get; init; }

    /// <summary>A cancellation whose event is still in the calendar; the card offers to remove it.</summary>
    public bool CanRemoveFromCalendar { get; init; }

    /// <summary>Localized reason shown when the card cannot offer answers; empty otherwise.</summary>
    public string UnavailableReason { get; init; } = string.Empty;
}
