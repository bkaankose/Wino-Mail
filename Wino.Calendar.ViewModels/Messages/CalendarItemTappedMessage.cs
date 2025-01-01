using Wino.Calendar.ViewModels.Data;

namespace Wino.Calendar.ViewModels.Messages
{
    public class CalendarItemTappedMessage
    {
        public CalendarItemTappedMessage(CalendarItemViewModel calendarItemViewModel)
        {
            CalendarItemViewModel = calendarItemViewModel;
        }

        public CalendarItemViewModel CalendarItemViewModel { get; }
    }
}
