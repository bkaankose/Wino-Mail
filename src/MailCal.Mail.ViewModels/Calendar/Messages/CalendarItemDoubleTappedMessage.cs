using MailCal.Calendar.ViewModels.Data;

namespace MailCal.Calendar.ViewModels.Messages;

public class CalendarItemDoubleTappedMessage
{
    public CalendarItemDoubleTappedMessage(CalendarItemViewModel calendarItemViewModel)
    {
        CalendarItemViewModel = calendarItemViewModel;
    }

    public CalendarItemViewModel CalendarItemViewModel { get; }
}
