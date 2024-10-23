using System;

namespace Wino.Messaging.Client.Calendar
{
    /// <summary>
    /// Raised when specific date is requested to be clicked on CalendarView.
    /// </summary>
    /// <param name="DateTime">Date to click.</param>
    public record ClickCalendarDateMessage(DateTime DateTime);
}
