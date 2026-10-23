using MailCal.Calendar.ViewModels.Data;

namespace MailCal.Calendar.ViewModels.Messages;

public class CalendarItemRightTappedMessage
{
    public CalendarItemRightTappedMessage(CalendarItemViewModel calendarItemViewModel)
    {
        CalendarItemViewModel = calendarItemViewModel;
    }

    public CalendarItemViewModel CalendarItemViewModel { get; }
}
