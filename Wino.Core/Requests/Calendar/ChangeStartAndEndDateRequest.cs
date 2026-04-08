using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Requests.Calendar;

/// <summary>
/// Request to move an existing calendar event by changing its start and end dates.
/// The item should already be updated in the local database before this request is queued.
/// </summary>
public record ChangeStartAndEndDateRequest(CalendarItem Item, List<CalendarEventAttendee> Attendees)
    : UpdateCalendarEventRequest(Item, Attendees)
{
    public override CalendarSynchronizerOperation Operation => CalendarSynchronizerOperation.ChangeStartAndEndDate;
}
