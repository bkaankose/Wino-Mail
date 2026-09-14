namespace Wino.Core.Domain.Enums;

/// <summary>
/// The kind of notification being raised. Kept separate from the notification host's own
/// application enum so notification policy stays in the domain layer.
/// </summary>
public enum NotificationKind
{
    Other = 0,
    Mail = 1,
    CalendarReminder = 2,
    TaskReminder = 3
}
