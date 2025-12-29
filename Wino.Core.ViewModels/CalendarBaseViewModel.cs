using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Messaging.Client.Calendar;

namespace Wino.Core.ViewModels;

public class CalendarBaseViewModel : CoreBaseViewModel,
    IRecipient<CalendarItemAdded>,
    IRecipient<CalendarItemUpdated>,
    IRecipient<CalendarItemDeleted>
{
    public void Receive(CalendarItemAdded message) => OnCalendarItemAdded(message.CalendarItem);
    public void Receive(CalendarItemUpdated message) => OnCalendarItemUpdated(message.CalendarItem);
    public void Receive(CalendarItemDeleted message) => OnCalendarItemDeleted(message.CalendarItem);

    protected virtual void OnCalendarItemAdded(CalendarItem calendarItem) { }
    protected virtual void OnCalendarItemUpdated(CalendarItem calendarItem) { }
    protected virtual void OnCalendarItemDeleted(CalendarItem calendarItem) { }

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
