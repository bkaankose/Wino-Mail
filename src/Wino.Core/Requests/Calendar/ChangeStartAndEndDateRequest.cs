using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Requests.Calendar;

/// <summary>
/// Request to move an existing calendar event by changing its start and end dates.
/// The item carries the new times; the database keeps the old ones until the server accepts the move.
/// </summary>
public record ChangeStartAndEndDateRequest(
    CalendarItem Item,
    List<CalendarEventAttendee> Attendees,
    List<Reminder> Reminders = null)
    : UpdateCalendarEventRequest(Item, Attendees, Reminders)
{
    public override CalendarSynchronizerOperation Operation => CalendarSynchronizerOperation.ChangeStartAndEndDate;
}
