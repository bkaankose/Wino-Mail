using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.Client.Calendar;

namespace Wino.Core.Requests.Calendar;

/// <summary>
/// Request to update personal event options without sending a full event edit.
/// </summary>
public record UpdateCalendarEventPersonalOptionsRequest(CalendarItem Item, List<Reminder> Reminders) : CalendarRequestBase(Item)
{
    public CalendarItem OriginalItem { get; init; }
    public List<Reminder> OriginalReminders { get; init; }

    public override CalendarSynchronizerOperation Operation => CalendarSynchronizerOperation.UpdateEventPersonalOptions;

    public override int ResynchronizationDelay => 2000;

    public override void ApplyUIChanges()
    {
        WeakReferenceMessenger.Default.Send(new CalendarItemUpdated(Item, EntityUpdateSource.ClientUpdated));
    }

    public override void RevertUIChanges()
    {
        WeakReferenceMessenger.Default.Send(new CalendarItemUpdated(OriginalItem ?? Item, EntityUpdateSource.ClientReverted));
    }
}
