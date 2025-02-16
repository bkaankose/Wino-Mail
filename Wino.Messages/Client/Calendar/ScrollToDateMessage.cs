using System;

namespace Wino.Messaging.Client.Calendar
{
    /// <summary>
    /// Raised when requested date is already loaded into calendar flip view to scroll to it.
    /// </summary>
    /// <param name="Date">Date to scroll.</param>
    public record ScrollToDateMessage(DateTime Date);
}
