namespace Wino.Core.Domain.Enums;

/// <summary>
/// System sound events supported by Wino app notifications.
/// Values match Microsoft.Windows.AppNotifications.Builder.AppNotificationSoundEvent.
/// </summary>
public enum NotificationSoundEvent
{
    Default = 0,
    IM = 1,
    Mail = 2,
    Reminder = 3,
    SMS = 4
}
