using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.ViewModels.Messages;

public class CalendarItemTappedMessage
{
    public CalendarItemTappedMessage(CalendarItemViewModel calendarItemViewModel, CalendarDayModel clickedPeriod)
    {
        CalendarItemViewModel = calendarItemViewModel;
        ClickedPeriod = clickedPeriod;
    }

    public CalendarItemViewModel CalendarItemViewModel { get; }
    public CalendarDayModel ClickedPeriod { get; }
}
