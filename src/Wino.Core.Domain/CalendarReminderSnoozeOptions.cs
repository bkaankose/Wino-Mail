using System;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Core.Domain;

public static class CalendarReminderSnoozeOptions
{
    private static readonly int[] SupportedSnoozeMinutes = [5, 10, 15, 30];

    public static IReadOnlyList<int> GetSupportedSnoozeMinutes()
        => SupportedSnoozeMinutes;

    public static IReadOnlyList<int> GetAllowedSnoozeMinutes(long reminderDurationInSeconds, long defaultReminderDurationInSeconds)
    {
        var reminderMinutes = (int)Math.Max(0, reminderDurationInSeconds / 60);

        if (reminderMinutes <= 0)
            return [];

        var maxSnoozeMinutes = reminderMinutes;
        var defaultReminderMinutes = (int)Math.Max(0, defaultReminderDurationInSeconds / 60);

        if (defaultReminderMinutes > 0)
            maxSnoozeMinutes = Math.Min(maxSnoozeMinutes, defaultReminderMinutes);

        return SupportedSnoozeMinutes.Where(minutes => minutes <= maxSnoozeMinutes).ToArray();
    }

    /// <summary>
    /// The snooze length a reminder offers by default: the preferred length when the reminder allows it,
    /// otherwise the shortest allowed one (Windows NotificationBuilder rule). Null when no snooze is allowed.
    /// </summary>
    public static int? GetDefaultSnoozeMinutes(long reminderDurationInSeconds, long defaultReminderDurationInSeconds, int preferredMinutes)
    {
        var allowed = GetAllowedSnoozeMinutes(reminderDurationInSeconds, defaultReminderDurationInSeconds);
        if (allowed.Count == 0)
            return null;

        return allowed.Contains(preferredMinutes) ? preferredMinutes : allowed[0];
    }
}
