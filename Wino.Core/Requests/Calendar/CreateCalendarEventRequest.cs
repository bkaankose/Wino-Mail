using System;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Helpers;
using Wino.Messaging.Client.Calendar;

namespace Wino.Core.Requests.Calendar;

/// <summary>
/// Request to create a new calendar event on the server.
/// Non-recurring events create an optimistic in-memory item for immediate UI feedback.
/// Recurring events skip optimistic rendering and rely on provider synchronization to materialize instances.
/// </summary>
public record CreateCalendarEventRequest : CalendarRequestBase
{
    public CalendarEventComposeResult ComposeResult { get; }
    public AccountCalendar AssignedCalendar { get; }
    public PreparedCalendarEventCreateModel PreparedEvent { get; }
    public CalendarItem PreparedItem => PreparedEvent.CalendarItem;
    public bool IsRecurring => !string.IsNullOrWhiteSpace(ComposeResult?.Recurrence);

    public CreateCalendarEventRequest(CalendarEventComposeResult composeResult, AccountCalendar assignedCalendar)
        : this(composeResult, assignedCalendar, CalendarEventComposeMapper.Prepare(composeResult, assignedCalendar))
    {
    }

    private CreateCalendarEventRequest(
        CalendarEventComposeResult composeResult,
        AccountCalendar assignedCalendar,
        PreparedCalendarEventCreateModel preparedEvent)
        : base(ShouldCreateOptimisticItem(composeResult) ? preparedEvent.CalendarItem : null)
    {
        ComposeResult = composeResult ?? throw new ArgumentNullException(nameof(composeResult));
        AssignedCalendar = assignedCalendar ?? throw new ArgumentNullException(nameof(assignedCalendar));
        PreparedEvent = preparedEvent ?? throw new ArgumentNullException(nameof(preparedEvent));
    }

    public override CalendarSynchronizerOperation Operation => CalendarSynchronizerOperation.CreateEvent;

    public override int ResynchronizationDelay => 5000;

    public override void ApplyUIChanges()
    {
        if (Item == null)
            return;

        WeakReferenceMessenger.Default.Send(new CalendarItemAdded(Item));
    }

    public override void RevertUIChanges()
    {
        if (Item == null)
            return;

        WeakReferenceMessenger.Default.Send(new CalendarItemDeleted(Item));
    }

    private static bool ShouldCreateOptimisticItem(CalendarEventComposeResult composeResult)
        => string.IsNullOrWhiteSpace(composeResult?.Recurrence);
}
