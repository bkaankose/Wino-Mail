using System;

namespace Wino.Core.Domain.Enums;

/// <summary>
/// Days the quiet hours schedule runs on.
/// </summary>
[Flags]
public enum QuietHoursDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    All = Weekdays | Weekend
}
