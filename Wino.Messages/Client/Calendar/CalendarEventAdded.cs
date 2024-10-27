using Wino.Core.Domain.Interfaces;

namespace Wino.Messaging.Client.Calendar
{
    /// <summary>
    /// Raised when event is added to database.
    /// </summary>
    public record CalendarEventAdded(ICalendarItem CalendarItem);
}
