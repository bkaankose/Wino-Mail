using System;
using Itenso.TimePeriod;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Calendar
{
    public class CalendarItem : ICalendarItem
    {
        public string Title { get; set; }
        public CalendarItem(DateTime startTime, DateTime endTime)
        {
            StartTime = startTime;
            EndTime = endTime;
            Period = new TimeRange(startTime, endTime);
        }

        public DateTimeOffset StartTime { get; }
        public DateTimeOffset EndTime { get; }

        public Guid Id { get; } = Guid.NewGuid();

        public TimeRange Period { get; }
    }
}
