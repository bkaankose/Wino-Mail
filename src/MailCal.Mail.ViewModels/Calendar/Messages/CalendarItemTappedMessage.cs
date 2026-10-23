using MailCal.Calendar.ViewModels.Data;
namespace MailCal.Calendar.ViewModels.Messages;

public class CalendarItemTappedMessage
{
    public CalendarItemTappedMessage(CalendarItemViewModel calendarItemViewModel)
    {
        CalendarItemViewModel = calendarItemViewModel;
    }

    public CalendarItemViewModel CalendarItemViewModel { get; }
}
