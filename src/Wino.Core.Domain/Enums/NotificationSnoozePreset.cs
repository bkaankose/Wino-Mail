namespace Wino.Core.Domain.Enums;

/// <summary>
/// Durations offered when notifications are snoozed. <see cref="None"/> means notifications are delivering.
/// </summary>
public enum NotificationSnoozePreset
{
    None = 0,
    ThirtyMinutes = 1,
    OneHour = 2,
    TwoHours = 3,
    RestOfDay = 4,
    UntilTomorrowMorning = 5,
    UntilTurnedBackOn = 6,
    Custom = 7
}
