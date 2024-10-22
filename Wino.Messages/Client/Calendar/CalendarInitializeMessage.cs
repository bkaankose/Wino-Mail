using System;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Messaging.Client.Calendar
{
    /// <summary>
    /// Raised when a new calendar range is requested for drawing.
    /// </summary>
    /// <param name="VisibleDateRange">Minimum and maximum date that is displayed in CalendarView.</param>
    /// <param name="DisplayType">Type of the calendar.</param>
    /// <param name="DisplayDate">Exact date to highlight.</param>
    /// <param name="DayDisplayCount">How many days to load with Day calendar display type.</param>
    public record CalendarInitializeMessage(DateRange VisibleDateRange,
                                            CalendarDisplayType DisplayType,
                                            DateTime DisplayDate,
                                            int DayDisplayCount = 7);
}
