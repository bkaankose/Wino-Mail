using System;

namespace Wino.Core.Domain.Exceptions;

/// <summary>A calendar operation targeted a read-only calendar, so nothing was queued.</summary>
public class ReadOnlyCalendarException(Guid calendarId)
    : Exception($"Calendar {calendarId} is read-only.")
{
    public Guid CalendarId { get; } = calendarId;
}
