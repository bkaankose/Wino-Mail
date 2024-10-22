using System;

namespace Wino.Core.Domain.Models.Calendar
{
    /// <summary>
    /// Contains the clicked date and the boundry dates of the calendar view.
    /// </summary>
    /// <param name="BoundryDates">Min - max visible dates on the control/</param>
    /// <param name="ClickedDate">Requested date.</param>
    public record CalendarViewDayClickedEventArgs(DateRange BoundryDates, DateTime ClickedDate);
}
