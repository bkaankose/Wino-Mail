using System;
using Wino.Core.Domain.Entities.Calendar;

namespace Wino.Core.Domain.Models.Calendar;

public sealed class CalendarReminderNotificationRequest
{
    public CalendarItem CalendarItem { get; init; } = null!;
    public long ReminderDurationInSeconds { get; init; }
    public string ReminderKey { get; init; } = string.Empty;

    /// <summary>When the reminder fires, in local time, snoozes included.</summary>
    public DateTime TriggerLocal { get; init; }
}
