using System;
using Itenso.TimePeriod;
using SQLite;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Entities.Calendar
{
    public class CalendarItem : ICalendarItem
    {
        [PrimaryKey]
        public Guid Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Location { get; set; }
        public DateTimeOffset StartTime { get; set; }
        public int DurationInMinutes { get; set; }
        public string Recurrence { get; set; }
        public CalendarItemStatus Status { get; set; }
        public CalendarItemVisibility Visibility { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public Guid CalendarId { get; set; }

        [Ignore]
        public TimeRange Period => new TimeRange(StartTime.Date, StartTime.Date.AddMinutes(DurationInMinutes));

        [Ignore]
        public IAccountCalendar AssignedCalendar { get; set; }
    }
}
