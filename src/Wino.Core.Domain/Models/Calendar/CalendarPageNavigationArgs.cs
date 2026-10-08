#nullable enable
using System;

namespace Wino.Core.Domain.Models.Calendar;

public class CalendarPageNavigationArgs
{
    /// <summary>
    /// When the app launches, automatically request the default calendar navigation options.
    /// </summary>
    public bool RequestDefaultNavigation { get; set; }

    /// <summary>
    /// Display the calendar view for the specified date.
    /// </summary>
    public DateTime NavigationDate { get; set; }

    /// <summary>
    /// Force reloading the calendar data even when the target range does not change.
    /// </summary>
    public bool ForceReload { get; set; }

    /// <summary>
    /// Optional event target to navigate to after the calendar page loads the requested range.
    /// </summary>
    public CalendarItemTarget? PendingTarget { get; set; }
}
