using System;

namespace Wino.Messaging.Client.Calendar
{
    /// <summary>
    /// Raised when a new date is picked from the CalendarView in shell.
    /// </summary>
    /// <param name="ClickedDate">Picked date.</param>
    public record CalendarDateClickedMessage(DateTime ClickedDate);
}
