using System;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Messaging.Client.Calendar;

namespace Wino.Core.ViewModels;

public class CalendarBaseViewModel : CoreBaseViewModel,
    IRecipient<CalendarItemAdded>,
    IRecipient<CalendarItemUpdated>,
    IRecipient<CalendarItemDeleted>
{
    public void Receive(CalendarItemAdded message) => DispatchToUIThread(() => OnCalendarItemAdded(message.CalendarItem));
    public void Receive(CalendarItemUpdated message) => DispatchToUIThread(() => OnCalendarItemUpdated(message.CalendarItem, message.Source));
    public void Receive(CalendarItemDeleted message) => DispatchToUIThread(() => OnCalendarItemDeleted(message.CalendarItem));

    protected virtual void OnCalendarItemAdded(CalendarItem calendarItem) { }
    protected virtual void OnCalendarItemUpdated(CalendarItem calendarItem, CalendarItemUpdateSource source) { }
    protected virtual void OnCalendarItemDeleted(CalendarItem calendarItem) { }

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
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<CalendarItemAdded>(this);
        Messenger.Unregister<CalendarItemUpdated>(this);
        Messenger.Unregister<CalendarItemDeleted>(this);
    }
}

