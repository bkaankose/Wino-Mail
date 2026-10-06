using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Messaging.Client.Calendar;

namespace Wino.Core.ViewModels;

public partial class CalendarBaseViewModel : CoreBaseViewModel,
    IRecipient<CalendarItemAdded>,
    IRecipient<CalendarItemUpdated>,
    IRecipient<CalendarItemDeleted>,
    IRecipient<CalendarItemOperationsCompleted>
{
    public void Receive(CalendarItemAdded message) => DispatchToUIThread(() => OnCalendarItemAdded(message.CalendarItem, message.Source));
    public void Receive(CalendarItemUpdated message) => DispatchToUIThread(() => OnCalendarItemUpdated(message.CalendarItem, message.Source));
    public void Receive(CalendarItemDeleted message) => DispatchToUIThread(() => OnCalendarItemDeleted(message.CalendarItem, message.Source));
    public void Receive(CalendarItemOperationsCompleted message) => DispatchToUIThread(() => OnCalendarItemOperationsCompleted(message.AccountId, message.CalendarItemIds));

    protected virtual void OnCalendarItemAdded(CalendarItem calendarItem, EntityUpdateSource source) { }
    protected virtual void OnCalendarItemUpdated(CalendarItem calendarItem, EntityUpdateSource source) { }
    protected virtual void OnCalendarItemDeleted(CalendarItem calendarItem, EntityUpdateSource source) { }

    /// <summary>
    /// Queued requests for these local calendar item ids finished executing. Items marked busy by
    /// a <see cref="EntityUpdateSource.ClientUpdated"/> change must leave the busy state here.
    /// </summary>
    protected virtual void OnCalendarItemOperationsCompleted(Guid accountId, IReadOnlyCollection<Guid> calendarItemIds) { }

    private void DispatchToUIThread(Action action)
    {
        _ = ExecuteUIThread(action);
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        Messenger.Register<CalendarItemAdded>(this);
        Messenger.Register<CalendarItemUpdated>(this);
        Messenger.Register<CalendarItemDeleted>(this);
        Messenger.Register<CalendarItemOperationsCompleted>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<CalendarItemAdded>(this);
        Messenger.Unregister<CalendarItemUpdated>(this);
        Messenger.Unregister<CalendarItemDeleted>(this);
        Messenger.Unregister<CalendarItemOperationsCompleted>(this);
    }
}
