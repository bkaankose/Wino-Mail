using System;
using Itenso.TimePeriod;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Domain.Interfaces;

public interface ICalendarItem
{
    string Title { get; }
    Guid Id { get; }
    IAccountCalendar AssignedCalendar { get; }
    DateTime StartDate { get; set; }
    DateTime EndDate { get; }
    double DurationInSeconds { get; set; }
    ITimePeriod Period { get; }

    bool IsAllDayEvent { get; }
    bool IsMultiDayEvent { get; }

    bool IsRecurringChild { get; }
    bool IsRecurringParent { get; }
    bool IsRecurringEvent { get; }

    /// <summary>
    /// Gets the display title for this calendar item when rendered in a specific day.
    /// For multi-day events, includes start/end time indicators.
    /// </summary>
    /// <param name="displayingPeriod">The period of the day where this item is being rendered.</param>
    /// <param name="calendarSettings">Calendar settings for time formatting.</param>
    /// <returns>The formatted title string.</returns>
    string GetDisplayTitle(ITimePeriod displayingPeriod, CalendarSettings calendarSettings);
}
