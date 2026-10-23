using CommunityToolkit.Mvvm.Messaging;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Requests;
using MailCal.Messaging.Client.Calendar;

namespace MailCal.Core.Requests.Calendar;

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

    public override void ApplyUIChanges()
    {
        // Notify UI that the event was deleted
        WeakReferenceMessenger.Default.Send(new CalendarItemDeleted(Item, EntityUpdateSource.ClientUpdated));
    }

    public override void RevertUIChanges()
    {
        // If deletion fails, we should notify the UI to add it back
        WeakReferenceMessenger.Default.Send(new CalendarItemAdded(Item, EntityUpdateSource.ClientReverted));
    }
}
