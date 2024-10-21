using System;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Calendar
{
    public class CalendarItem : ICalendarItem
    {
        public CalendarItem(DateTime startTime, DateTime endTime)
        {
            StartTime = startTime;
            EndTime = endTime;
        }

        public DateTime StartTime { get; }
        public DateTime EndTime { get; }

        public Guid Id { get; } = Guid.NewGuid();
    }
}
