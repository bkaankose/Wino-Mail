using Wino.Calendar.ViewModels.Data;

namespace Wino.Calendar.Controls;

internal sealed class CalendarDragPackage
{
    public CalendarDragPackage(CalendarItemViewModel calendarItemViewModel)
    {
        CalendarItemViewModel = calendarItemViewModel;
    }

    public CalendarItemViewModel CalendarItemViewModel { get; }
}
