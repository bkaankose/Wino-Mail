using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

/// <summary>
/// Encapsulates the options for preparing calendar operation requests.
/// </summary>
/// <param name="Operation">Calendar operation to execute (Create, Update, Delete).</param>
/// <param name="CalendarItem">Calendar item to operate on.</param>
/// <param name="Attendees">List of attendees for the calendar event.</param>
public record CalendarOperationPreparationRequest(CalendarSynchronizerOperation Operation, CalendarItem CalendarItem, List<CalendarEventAttendee> Attendees);
