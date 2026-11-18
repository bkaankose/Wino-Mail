
using Wino.Mail.ViewModels.Calendar.Data;

namespace Wino.Mail.ViewModels.Calendar.Messages;

public class CalendarItemRightTappedMessage
{
    public CalendarItemRightTappedMessage(CalendarItemViewModel calendarItemViewModel)
    {
        CalendarItemViewModel = calendarItemViewModel;
    }

    public CalendarItemViewModel CalendarItemViewModel { get; }
}
