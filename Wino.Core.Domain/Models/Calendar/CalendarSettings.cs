using System;
using System.Collections.Generic;
using System.Globalization;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar
{
    public class CalendarSettings
    {
        public DayOfWeek FirstDayOfWeek { get; set; }
        public List<DayOfWeek> WorkingDays { get; set; }
        public TimeSpan WorkingHourStart { get; set; }
        public TimeSpan WorkingHourEnd { get; set; }
        public double HourHeight { get; set; }
        public DayHeaderDisplayType DayHeaderDisplayType { get; set; }
        public CultureInfo CultureInfo { get; }
    }
}
