using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.Client.Calendar;

namespace Wino.Core.Requests.Calendar;

/// <summary>
/// Request to update an existing calendar event on the server.
/// The calendar item should be already updated in the local database before queuing this request.
/// </summary>
public record UpdateCalendarEventRequest(CalendarItem Item, List<CalendarEventAttendee> Attendees) : CalendarRequestBase(Item)
{
    /// <summary>
    /// Original attendees before the update, used for reverting changes if the update fails.
    /// </summary>
    public List<CalendarEventAttendee> OriginalAttendees { get; init; }

    /// <summary>
    /// Original calendar item state before the update, used for reverting changes if the update fails.
    /// </summary>
    public CalendarItem OriginalItem { get; init; }

    public override CalendarSynchronizerOperation Operation => CalendarSynchronizerOperation.UpdateEvent;

    /// <summary>
    /// After successful update, we need to resync to ensure changes are properly reflected.
    /// </summary>
    public override int ResynchronizationDelay => 2000;

    public override void ApplyUIChanges()
    {
        // Notify UI that the event was updated locally
        WeakReferenceMessenger.Default.Send(new CalendarItemUpdated(Item));
    }

    public override void RevertUIChanges()
    {
        // If update fails, restore the original state
        if (OriginalItem != null && OriginalAttendees != null)
        {
            // Send the original item back to restore UI state
            WeakReferenceMessenger.Default.Send(new CalendarItemUpdated(OriginalItem));
        }
        else
        {
            // Fallback: just notify with current item to trigger refresh
            WeakReferenceMessenger.Default.Send(new CalendarItemUpdated(Item));
        }
    }
}
