using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Calendar;

public class Reminder
{
    [PrimaryKey]
    public Guid Id { get; set; }
    public Guid CalendarItemId { get; set; }

    public DateTimeOffset ReminderTime { get; set; }
    public CalendarItemReminderType ReminderType { get; set; }
}
