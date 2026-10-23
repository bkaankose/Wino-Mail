using MailCal.Core.Domain.Entities.Calendar;

namespace MailCal.Core.Domain.Models.Calendar;

public sealed class CalendarReminderNotificationRequest
{
    public CalendarItem CalendarItem { get; init; } = null!;
    public long ReminderDurationInSeconds { get; init; }
    public string ReminderKey { get; init; } = string.Empty;
}
