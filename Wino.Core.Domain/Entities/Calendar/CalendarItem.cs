using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Calendar
{
    public class CalendarItem
    {
        [PrimaryKey]
        public Guid Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Location { get; set; }
        public DateTimeOffset StartTime { get; set; }
        public DateTimeOffset EndTime { get; set; }
        public bool IsAllDay { get; set; }
        public Guid? RecurrenceRuleId { get; set; }
        public CalendarItemStatus Status { get; set; }
        public CalendarItemVisibility Visibility { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public Guid CalendarId { get; set; }
    }
}
