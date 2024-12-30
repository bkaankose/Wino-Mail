using Wino.Core.Domain.Entities.Calendar;

namespace Wino.Messaging.Client.Calendar
{
    /// <summary>
    /// Raised when event is added to database.
    /// </summary>
    public record CalendarEventAdded(CalendarItem CalendarItem);
}
