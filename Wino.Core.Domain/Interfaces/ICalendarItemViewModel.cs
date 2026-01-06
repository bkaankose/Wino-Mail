using Itenso.TimePeriod;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Temporarily to enforce CalendarItemViewModel. Used in CalendarEventCollection.
/// </summary>
public interface ICalendarItemViewModel
{
    bool IsSelected { get; set; }

    /// <summary>
    /// The period of the day where this item is currently being displayed.
    /// </summary>
    ITimePeriod DisplayingPeriod { get; set; }

    /// <summary>
    /// Calendar settings for time formatting.
    /// </summary>
    CalendarSettings CalendarSettings { get; set; }

    /// <summary>
    /// Updates the view model's underlying CalendarItem from new data.
    /// This allows in-place updates without removing and re-adding items.
    /// </summary>
    /// <param name="calendarItem">The updated calendar item data.</param>
    void UpdateFrom(CalendarItem calendarItem);
}
