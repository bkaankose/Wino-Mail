using System;
using MailCal.Core.Domain.Enums;

namespace MailCal.Calendar.Controls;

/// <summary>
/// A period on the rail: raised when the rail settles on one the view model has not loaded, and
/// while it moves, whenever the period nearest to the viewport changes.
/// </summary>
public sealed class CalendarPeriodEventArgs : EventArgs
{
    public CalendarPeriodEventArgs(CalendarDisplayType displayType, DateOnly anchorDate)
    {
        DisplayType = displayType;
        AnchorDate = anchorDate;
    }

    public CalendarDisplayType DisplayType { get; }

    /// <summary>
    /// The anchor to resolve with <see cref="MailCal.Core.Domain.Models.Calendar.CalendarRangeResolver"/>; the
    /// resolved range starts on the unit the rail settled on.
    /// </summary>
    public DateOnly AnchorDate { get; }
}
