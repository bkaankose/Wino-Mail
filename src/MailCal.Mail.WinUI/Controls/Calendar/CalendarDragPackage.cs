using MailCal.Calendar.ViewModels.Data;

namespace MailCal.Calendar.Controls;

internal sealed class CalendarDragPackage
{
    public CalendarDragPackage(CalendarItemViewModel calendarItemViewModel)
    {
        CalendarItemViewModel = calendarItemViewModel;
    }

    public CalendarItemViewModel CalendarItemViewModel { get; }
}
