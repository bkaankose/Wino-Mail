using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

/// <summary>
/// Encapsulates the options for preparing calendar operation requests.
/// </summary>
/// <param name="Operation">Calendar operation to execute (Create, Update, Delete, Accept, Decline, Tentative).</param>
/// <param name="CalendarItem">Calendar item to operate on.</param>
/// <param name="Attendees">List of attendees for the calendar event.</param>
/// <param name="ResponseMessage">Optional message to include with event responses (Accept, Decline, Tentative).</param>
/// <param name="OriginalItem">Original calendar item state before update (for revert capability).</param>
/// <param name="OriginalAttendees">Original attendees list before update (for revert capability).</param>
public record CalendarOperationPreparationRequest(
    CalendarSynchronizerOperation Operation,
    CalendarItem CalendarItem,
    List<CalendarEventAttendee> Attendees,
    string ResponseMessage = null,
    CalendarItem OriginalItem = null,
    List<CalendarEventAttendee> OriginalAttendees = null);
