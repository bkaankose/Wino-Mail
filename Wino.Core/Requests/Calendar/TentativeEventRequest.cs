using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.Client.Calendar;

namespace Wino.Core.Requests.Calendar;

/// <summary>
/// Request to tentatively accept a calendar event invitation on the server.
/// The calendar item status should be updated locally before queuing this request.
/// </summary>
public record TentativeEventRequest(CalendarItem Item, string ResponseMessage = null) : CalendarRequestBase(Item)
{
    private readonly CalendarItemStatus _previousStatus = Item.Status;

    public override CalendarSynchronizerOperation Operation => CalendarSynchronizerOperation.TentativeEvent;

    /// <summary>
    /// After successful tentative acceptance, we need to resync to get updated status.
    /// </summary>
    public override int ResynchronizationDelay => 2000;

    public override void ApplyUIChanges()
    {
        // Update the item status locally
        Item.Status = CalendarItemStatus.Tentative;
        
        // Notify UI that the event status was updated
        WeakReferenceMessenger.Default.Send(new CalendarItemUpdated(Item));
    }

    public override void RevertUIChanges()
    {
        // If tentative acceptance fails, revert to the previous status
        Item.Status = _previousStatus;
        WeakReferenceMessenger.Default.Send(new CalendarItemUpdated(Item));
    }
}
