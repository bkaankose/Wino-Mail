using System.Collections.Generic;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Requests.Calendar;

/// <summary>
/// Request to move an existing calendar event by changing its start and end dates.
/// The item should already be updated in the local database before this request is queued.
/// </summary>
public record ChangeStartAndEndDateRequest(
    CalendarItem Item,
    List<CalendarEventAttendee> Attendees,
    List<Reminder> Reminders = null)
    : UpdateCalendarEventRequest(Item, Attendees, Reminders)
{
    public override CalendarSynchronizerOperation Operation => CalendarSynchronizerOperation.ChangeStartAndEndDate;
}
