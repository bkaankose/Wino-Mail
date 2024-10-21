using System;
using System.Collections.Generic;
using System.Globalization;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar
{
    public class CalendarRenderOptions
    {

        public CalendarRenderOptions(DateTime startDate,
                                     DateTime endDate,
                                     List<DayOfWeek> workingDays,
                                     TimeSpan workDayStart,
                                     TimeSpan workDayEnd,
                                     double hourHeight,
                                     DayHeaderDisplayType dayHeaderDisplayType,
                                     CultureInfo cultureInfo)
        {
            StartDate = startDate;
            EndDate = endDate;
            WorkingDays = workingDays;
            WorkDayStart = workDayStart;
            WorkDayEnd = workDayEnd;
            HourHeight = hourHeight;
            DayHeaderDisplayType = dayHeaderDisplayType;
            CultureInfo = cultureInfo;
        }
        public int TotalDayCount => (EndDate - StartDate).Days;
        public DateTime StartDate { get; }
        public DateTime EndDate { get; }
        public List<DayOfWeek> WorkingDays { get; }
        public TimeSpan WorkDayStart { get; }
        public TimeSpan WorkDayEnd { get; }
        public double HourHeight { get; }
        public DayHeaderDisplayType DayHeaderDisplayType { get; }
        public CultureInfo CultureInfo { get; }
    }
}
