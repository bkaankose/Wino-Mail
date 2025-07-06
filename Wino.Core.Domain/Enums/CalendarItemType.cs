using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Enums;
public enum CalendarItemType
{
    /// <summary>
    /// A standard timed event with specific start and end times on the same day
    /// </summary>
    Timed = 0,

    /// <summary>
    /// An all-day event that spans exactly one day
    /// </summary>
    AllDay = 1,

    /// <summary>
    /// A multi-day event that spans more than one day but has specific times
    /// </summary>
    MultiDay = 2,

    /// <summary>
    /// A multi-day all-day event (e.g., vacation, conference spanning multiple days)
    /// </summary>
    MultiDayAllDay = 3,

    /// <summary>
    /// A recurring event with a defined pattern (daily, weekly, monthly, yearly)
    /// </summary>
    Recurring = 4,

    /// <summary>
    /// A recurring all-day event (e.g., annual holiday, weekly all-day event)
    /// </summary>
    RecurringAllDay = 5,

    /// <summary>
    /// A single instance of a recurring event that has been modified
    /// </summary>
    RecurringException = 6,

    /// <summary>
    /// An event that extends beyond midnight but is not multi-day (e.g., 11 PM to 2 AM)
    /// </summary>
    CrossMidnight = 7,
}
