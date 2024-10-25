using Wino.Core.Domain.Enums;

namespace Wino.Messaging.Client.Calendar
{
    /// <summary>
    /// Raised when calendar mode is being changed.
    /// </summary>
    /// <param name="OldType">Old type.</param>
    /// <param name="NewType">New type.</param>
    public record CalendarDisplayModeChangedMessage(CalendarDisplayType OldType, CalendarDisplayType NewType);
}
