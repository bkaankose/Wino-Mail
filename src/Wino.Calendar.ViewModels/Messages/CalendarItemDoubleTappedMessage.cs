using Wino.Calendar.ViewModels.Data;

namespace Wino.Calendar.ViewModels.Messages;

public class CalendarItemDoubleTappedMessage
{
    public CalendarItemDoubleTappedMessage(CalendarItemViewModel calendarItemViewModel)
    {
        CalendarItemViewModel = calendarItemViewModel;
    }

    public CalendarItemViewModel CalendarItemViewModel { get; }
}
