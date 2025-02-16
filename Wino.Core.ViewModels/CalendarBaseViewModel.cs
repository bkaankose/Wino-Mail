using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Messaging.Client.Calendar;

namespace Wino.Core.ViewModels
{
    public class CalendarBaseViewModel : CoreBaseViewModel, IRecipient<CalendarItemAdded>
    {
        public void Receive(CalendarItemAdded message) => OnCalendarItemAdded(message.CalendarItem);

        protected virtual void OnCalendarItemAdded(CalendarItem calendarItem) { }
    }
}
