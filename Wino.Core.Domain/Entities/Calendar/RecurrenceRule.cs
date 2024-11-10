using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Calendar
{
    public class RecurrenceRule
    {
        [PrimaryKey]
        public Guid Id { get; set; }

        public Guid CalendarItemId { get; set; }
        public CalendarItemRecurrenceFrequency Frequency { get; set; }
        public int Interval { get; set; }
        public string DaysOfWeek { get; set; }
        public DateTimeOffset StartDate { get; set; }
        public DateTimeOffset EndDate { get; set; }
        public int Occurrences { get; set; }
    }
}
