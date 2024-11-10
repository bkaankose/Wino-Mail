using System;

namespace Wino.Messaging.Client.Calendar
{
    /// <summary>
    /// Raised when specific date is requested to be clicked on CalendarView.
    /// </summary>
    /// <param name="DateTime">Date to be navigated.</param>
    public record GoToCalendarDayMessage(DateTime DateTime);
}
