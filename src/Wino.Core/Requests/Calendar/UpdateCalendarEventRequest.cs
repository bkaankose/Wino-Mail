using Wino.Core.Domain.Interfaces;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Messaging.Client.Calendar;

namespace Wino.Core.Requests.Calendar;

/// <summary>
/// Request to update an existing calendar event on the server. <c>Item</c>, <c>Attendees</c> and
/// <c>Reminders</c> carry the wanted state; the caller does not store it.
/// The UI shows it at once, and the provider's success path stores it through
/// <c>IDefaultChangeProcessor.PersistUpdatedCalendarEventAsync</c> after the server accepts the edit.
/// </summary>
public record UpdateCalendarEventRequest(
    CalendarItem Item,
    List<CalendarEventAttendee> Attendees,
    List<Reminder> Reminders = null) : CalendarRequestBase(Item)
{
    /// <summary>
    /// Original attendees before the update, used for reverting changes if the update fails.
    /// </summary>
    public List<CalendarEventAttendee> OriginalAttendees { get; init; }

    /// <summary>
    /// Original calendar item state before the update, used for reverting changes if the update fails.
    /// </summary>
    public CalendarItem OriginalItem { get; init; }

    /// <summary>
    /// Original reminders before the update. The stored reminders stay as they are until the server accepts the edit.
    /// </summary>
    public List<Reminder> OriginalReminders { get; init; }

    /// <summary>
    /// The complete wanted attachment list, or null to leave attachments unchanged. A draft whose
    /// Id equals a stored CalendarAttachment.Id keeps that attachment; a draft with a FilePath is
    /// added; stored attachments missing from the list are removed (CalDAV managed attachments).
    /// </summary>
    public List<CalendarEventComposeAttachmentDraft> Attachments { get; init; }

    public override CalendarSynchronizerOperation Operation => CalendarSynchronizerOperation.UpdateEvent;

    /// <summary>
    /// After successful update, we need to resync to ensure changes are properly reflected.
    /// </summary>
    public override int ResynchronizationDelay => 2000;

    public override void ApplyUIChanges(IUIMessageSender sender)
    {
        sender.Send(new CalendarItemUpdated(Item, EntityUpdateSource.ClientUpdated));
    }

    public override void RevertUIChanges(IUIMessageSender sender)
    {
        sender.Send(new CalendarItemUpdated(OriginalItem ?? Item, EntityUpdateSource.ClientReverted));
    }
}
