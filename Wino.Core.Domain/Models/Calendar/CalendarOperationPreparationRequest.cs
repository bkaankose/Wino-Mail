using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

/// <summary>
/// Encapsulates the options for preparing calendar operation requests.
/// </summary>
/// <param name="Operation">Calendar operation to execute (Create, Update, ChangeStartAndEndDate, Delete, Accept, Decline, Tentative).</param>
/// <param name="CalendarItem">Calendar item to operate on.</param>
/// <param name="Attendees">List of attendees for the calendar event.</param>
/// <param name="ResponseMessage">Optional message to include with event responses (Accept, Decline, Tentative).</param>
/// <param name="OriginalItem">Original calendar item state before update (for revert capability).</param>
/// <param name="OriginalAttendees">Original attendees list before update (for revert capability).</param>
/// <param name="Reminders">
/// Reminders to persist for an update. A null value leaves provider reminders unchanged;
/// an empty list explicitly disables all reminders.
/// </param>
/// <param name="OriginalReminders">Original reminders before update, retained for revert parity.</param>
public record CalendarOperationPreparationRequest(
    CalendarSynchronizerOperation Operation,
    CalendarItem CalendarItem = null,
    List<CalendarEventAttendee> Attendees = null,
    string ResponseMessage = null,
    CalendarItem OriginalItem = null,
    List<CalendarEventAttendee> OriginalAttendees = null,
    CalendarEventComposeResult ComposeResult = null,
    List<Reminder> Reminders = null,
    List<Reminder> OriginalReminders = null);
