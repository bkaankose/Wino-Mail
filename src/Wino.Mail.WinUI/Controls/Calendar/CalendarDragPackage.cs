
using Wino.Mail.ViewModels.Calendar.Data;

namespace Wino.Mail.WinUI.Controls.Calendar;

internal sealed class CalendarDragPackage
{
    public CalendarDragPackage(CalendarItemViewModel calendarItemViewModel)
    {
        CalendarItemViewModel = calendarItemViewModel;
    }

    public CalendarItemViewModel CalendarItemViewModel { get; }
}
