using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.ViewModels.Data;

/// <summary>
/// Builds the reminder and snooze duration option lists shared by the calendar settings sections
/// and the merged notifications page. Kept here rather than on a calendar base class so both
/// consumers resolve the same durations from one implementation.
/// </summary>
public static class CalendarReminderOptionFactory
{
    public static IEnumerable<string> GetReminderOptions(ICalendarService calendarService)
    {
        yield return Translator.CalendarReminder_None;

        foreach (var minutes in calendarService.GetPredefinedReminderMinutes())
        {
            yield return minutes switch
            {
                >= 60 => string.Format(minutes / 60 == 1
                    ? Translator.CalendarReminder_HourOption
                    : Translator.CalendarReminder_HoursOption, minutes / 60),
                _ => string.Format(minutes == 1
                    ? Translator.CalendarReminder_MinuteOption
                    : Translator.CalendarReminder_MinutesOption, minutes)
            };
        }
    }

    public static int GetSelectedReminderIndex(ICalendarService calendarService, long defaultReminderDurationInSeconds)
    {
        if (defaultReminderDurationInSeconds == 0)
            return 0;

        var minutes = (int)(defaultReminderDurationInSeconds / 60);
        var index = Array.IndexOf(calendarService.GetPredefinedReminderMinutes(), minutes);

        return index >= 0 ? index + 1 : 0;
    }

    /// <summary>
    /// Converts a selected reminder index back to seconds. Index zero is the "no reminder" entry.
    /// </summary>
    public static long GetReminderDurationInSeconds(ICalendarService calendarService, int selectedIndex)
    {
        if (selectedIndex <= 0)
            return 0;

        var predefinedMinutes = calendarService.GetPredefinedReminderMinutes();

        if (selectedIndex > predefinedMinutes.Length)
            return 0;

        return predefinedMinutes[selectedIndex - 1] * 60L;
    }

    public static IEnumerable<string> GetSnoozeOptions()
        => CalendarReminderSnoozeOptions.GetSupportedSnoozeMinutes()
            .Select(minutes => string.Format(Translator.CalendarReminder_SnoozeMinutesOption, minutes));

    public static int GetSelectedSnoozeIndex(int defaultSnoozeDurationInMinutes)
    {
        var supportedSnoozeMinutes = CalendarReminderSnoozeOptions.GetSupportedSnoozeMinutes().ToArray();
        var selectedIndex = Array.IndexOf(supportedSnoozeMinutes, defaultSnoozeDurationInMinutes);

        return selectedIndex >= 0 ? selectedIndex : 0;
    }

    /// <summary>
    /// Converts a selected snooze index back to minutes. Returns null when no durations are supported.
    /// </summary>
    public static int? GetSnoozeMinutes(int selectedIndex)
    {
        var supportedSnoozeMinutes = CalendarReminderSnoozeOptions.GetSupportedSnoozeMinutes();

        if (supportedSnoozeMinutes.Count == 0)
            return null;

        return supportedSnoozeMinutes[Math.Clamp(selectedIndex, 0, supportedSnoozeMinutes.Count - 1)];
    }
}
