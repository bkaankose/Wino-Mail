using System;

namespace Wino.Messaging.Client.Calendar
{
    /// <summary>
    /// Emitted when vertical scroll position is requested to be changed.
    /// </summary>
    /// <param name="TimeSpan">Hour to scroll vertically on flip view item.</param>
    public record ScrollToHourMessage(TimeSpan TimeSpan);
}
