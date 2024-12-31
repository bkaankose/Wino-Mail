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

        public DateTime StartDate { get; set; }

        public DateTime EndDate
        {
            get
            {
                return StartDate.AddSeconds(DurationInSeconds);
            }
        }

        public TimeSpan StartDateOffset { get; set; }
        public TimeSpan EndDateOffset { get; set; }

        private ITimePeriod _period;
        public ITimePeriod Period
        {
            get
            {
                _period ??= new TimeRange(StartDate, EndDate);

                return _period;
            }
        }

        public bool IsAllDayEvent
        {
            get
            {
                return StartDate.TimeOfDay == TimeSpan.Zero && EndDate.TimeOfDay == TimeSpan.Zero;
            }
        }

        public bool IsMultiDayEvent
        {
            get
            {
                return StartDate.Date != EndDate.Date;
            }
        }

        public double DurationInSeconds { get; set; }
        public string Recurrence { get; set; }

        // TODO
        public string CustomEventColorHex { get; set; }
        public string HtmlLink { get; set; }
        public CalendarItemStatus Status { get; set; }
        public CalendarItemVisibility Visibility { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public Guid CalendarId { get; set; }

        [Ignore]
        public IAccountCalendar AssignedCalendar { get; set; }
    }
}
