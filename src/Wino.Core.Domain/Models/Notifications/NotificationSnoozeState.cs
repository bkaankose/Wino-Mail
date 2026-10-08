using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Notifications;

/// <summary>
/// The evaluated snooze state at a point in time.
/// </summary>
/// <param name="IsSnoozed">Whether notifications are currently held.</param>
/// <param name="Preset">The preset that started the snooze, or <see cref="NotificationSnoozePreset.None"/>.</param>
/// <param name="Until">When the snooze ends, or null when it runs until it is turned back on.</param>
public sealed record NotificationSnoozeState(bool IsSnoozed, NotificationSnoozePreset Preset, DateTimeOffset? Until)
{
    public static NotificationSnoozeState Delivering { get; } = new(false, NotificationSnoozePreset.None, null);
}
