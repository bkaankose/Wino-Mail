using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.Client.Calendar;

namespace Wino.Core.Requests.Calendar;

/// <summary>
/// Request to create a new calendar event on the server.
/// The calendar item should be already saved to the local database before queuing this request.
/// </summary>
public record CreateCalendarEventRequest(CalendarItem Item, List<CalendarEventAttendee> Attendees) : CalendarRequestBase(Item)
{
    public override CalendarSynchronizerOperation Operation => CalendarSynchronizerOperation.CreateEvent;

    /// <summary>
    /// After successful creation, we need to resync to get the remote event ID.
    /// </summary>
    public override int ResynchronizationDelay => 2000;

    public override void ApplyUIChanges()
    {
        // Notify UI that the event was created locally
        WeakReferenceMessenger.Default.Send(new CalendarItemAdded(Item));
    }

    public override void RevertUIChanges()
    {
        // If creation fails, we should notify the UI to remove it
        WeakReferenceMessenger.Default.Send(new CalendarItemDeleted(Item));
    }
}
