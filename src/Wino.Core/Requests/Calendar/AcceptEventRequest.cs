using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Messaging.Client.Calendar;

namespace Wino.Core.Requests.Calendar;

/// <summary>
/// Request to accept a calendar event invitation on the server.
/// The status changes in memory only; the stored status follows from the provider after the server accepts the answer.
/// </summary>
public record AcceptEventRequest(CalendarItem Item, string ResponseMessage = null) : CalendarRequestBase(Item)
{
    private readonly CalendarItemStatus _previousStatus = Item.Status;

    public override CalendarSynchronizerOperation Operation => CalendarSynchronizerOperation.AcceptEvent;

    /// <summary>
    /// After successful acceptance, we need to resync to get updated status.
    /// </summary>
    public override int ResynchronizationDelay => 2000;

    public override void ApplyUIChanges(IUIMessageSender sender)
    {
        // Update the item status locally
        Item.Status = CalendarItemStatus.Accepted;
        
        // Notify UI that the event status was updated
        sender.Send(new CalendarItemUpdated(Item, EntityUpdateSource.ClientUpdated));
    }

    public override void RevertUIChanges(IUIMessageSender sender)
    {
        // If acceptance fails, revert to the previous status
        Item.Status = _previousStatus;
        sender.Send(new CalendarItemUpdated(Item, EntityUpdateSource.ClientReverted));
    }
}
