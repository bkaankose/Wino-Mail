using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Messaging.Client.Calendar;

namespace Wino.Core.Requests.Calendar;

/// <summary>
/// Request to delete a calendar event on the server.
/// </summary>
public record DeleteCalendarEventRequest(CalendarItem Item) : CalendarRequestBase(Item)
{
    public override CalendarSynchronizerOperation Operation => CalendarSynchronizerOperation.DeleteEvent;

    /// <summary>
    /// After successful deletion, resync to confirm the event was removed.
    /// </summary>
    public override int ResynchronizationDelay => 2000;

    public override void ApplyUIChanges(IUIMessageSender sender)
    {
        // Notify UI that the event was deleted
        sender.Send(new CalendarItemDeleted(Item, EntityUpdateSource.ClientUpdated));
    }

    public override void RevertUIChanges(IUIMessageSender sender)
    {
        // If deletion fails, we should notify the UI to add it back
        sender.Send(new CalendarItemAdded(Item, EntityUpdateSource.ClientReverted));
    }
}
