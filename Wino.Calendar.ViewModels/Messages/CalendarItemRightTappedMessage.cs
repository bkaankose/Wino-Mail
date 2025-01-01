using Wino.Calendar.ViewModels.Data;

namespace Wino.Calendar.ViewModels.Messages
{
    public class CalendarItemRightTappedMessage
    {
        public CalendarItemRightTappedMessage(CalendarItemViewModel calendarItemViewModel)
        {
            CalendarItemViewModel = calendarItemViewModel;
        }

        public CalendarItemViewModel CalendarItemViewModel { get; }
    }
}
