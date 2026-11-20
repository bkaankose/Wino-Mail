using Wino.Mail.ViewModels.Calendar.Data;

namespace Wino.Mail.ViewModels.Calendar.Messages;

public class CalendarItemTappedMessage
{
    public CalendarItemTappedMessage(CalendarItemViewModel calendarItemViewModel)
    {
        CalendarItemViewModel = calendarItemViewModel;
    }

    public CalendarItemViewModel CalendarItemViewModel { get; }
}
