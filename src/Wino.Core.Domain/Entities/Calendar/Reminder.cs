using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Calendar;

public class Reminder
{
    [PrimaryKey]
    public Guid Id { get; set; }
    public Guid CalendarItemId { get; set; }

    /// <summary>
    /// Duration in seconds before the event start time when the reminder should trigger.
    /// For example, 900 seconds = 15 minutes before event.
    /// </summary>
    public long DurationInSeconds { get; set; }
    public CalendarItemReminderType ReminderType { get; set; }
}
