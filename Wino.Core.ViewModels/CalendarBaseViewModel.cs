using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Messaging.Client.Calendar;

namespace Wino.Core.ViewModels
{
    public class CalendarBaseViewModel : CoreBaseViewModel, IRecipient<CalendarEventAdded>
    {
        public void Receive(CalendarEventAdded message) => OnCalendarEventAdded(message.CalendarItem);

        protected virtual void OnCalendarEventAdded(CalendarItem calendarItem) { }
    }
}
