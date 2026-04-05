using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.Client.Calendar;

namespace Wino.Core.Requests.Calendar;

/// <summary>
/// Outlook-specific request to decline a calendar event invitation.
/// In Outlook, declined events are removed from the calendar by the API after synchronization,
/// so this request sends a delete notification to remove the event from the UI.
/// </summary>
public record OutlookDeclineEventRequest(CalendarItem Item, string ResponseMessage = null) : CalendarRequestBase(Item)
{
    private readonly CalendarItemStatus _previousStatus = Item.Status;

    public override CalendarSynchronizerOperation Operation => CalendarSynchronizerOperation.DeclineEvent;

    /// <summary>
    /// After successful decline, we need to resync to confirm the event is removed.
    /// </summary>
    public override int ResynchronizationDelay => 2000;

    public override void ApplyUIChanges()
    {
        // In Outlook, declined events are deleted from the calendar after sync
        // Send deleted message to remove from UI immediately
        WeakReferenceMessenger.Default.Send(new CalendarItemDeleted(Item));
    }

    public override void RevertUIChanges()
    {
        // If decline fails, restore the previous status and re-add the event
        Item.Status = _previousStatus;
        WeakReferenceMessenger.Default.Send(new CalendarItemAdded(Item));
    }
}
