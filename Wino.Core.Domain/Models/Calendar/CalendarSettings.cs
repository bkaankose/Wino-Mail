using System;
using System.Collections.Generic;
using System.Globalization;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar
{
    public record CalendarSettings(DayOfWeek FirstDayOfWeek,
                                   List<DayOfWeek> WorkingDays,
                                   TimeSpan WorkingHourStart,
                                   TimeSpan WorkingHourEnd,
                                   double HourHeight,
                                   DayHeaderDisplayType DayHeaderDisplayType,
                                   CultureInfo CultureInfo);
}
