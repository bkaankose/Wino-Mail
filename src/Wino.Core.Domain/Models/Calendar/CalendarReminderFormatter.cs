using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

public enum CalendarReminderUnit
{
    Minutes,
    Hours,
    Days,
    Weeks
}

/// <summary>
/// A reminder offset is seconds before the event start: positive before, negative after, zero at the start.
/// </summary>
public static class CalendarReminderFormatter
{
    public static long GetUnitSeconds(CalendarReminderUnit unit)
        => unit switch
        {
            CalendarReminderUnit.Weeks => 7 * 86400,
            CalendarReminderUnit.Days => 86400,
            CalendarReminderUnit.Hours => 3600,
            _ => 60
        };

    /// <summary>The largest unit that expresses the offset's size exactly, counted in whole minutes.</summary>
    public static (long Value, CalendarReminderUnit Unit) Decompose(long offsetSeconds)
    {
        var minutes = Math.Abs(offsetSeconds) / 60;
        if (minutes == 0)
            return (0, CalendarReminderUnit.Minutes);

        foreach (var unit in new[] { CalendarReminderUnit.Weeks, CalendarReminderUnit.Days, CalendarReminderUnit.Hours })
        {
            var unitMinutes = GetUnitSeconds(unit) / 60;
            if (minutes % unitMinutes == 0)
                return (minutes / unitMinutes, unit);
        }

        return (minutes, CalendarReminderUnit.Minutes);
    }

    public static string Format(long offsetSeconds, CalendarItemReminderType reminderType = CalendarItemReminderType.Popup)
    {
        var (value, unit) = Decompose(offsetSeconds);

        var text = value == 0
            ? Translator.CalendarReminder_AtStart
            : offsetSeconds < 0
                ? string.Format(Translator.CalendarReminder_AfterStartFormat, FormatAmount(value, unit))
                : FormatAmount(value, unit);

        return reminderType == CalendarItemReminderType.Email
            ? string.Format(Translator.CalendarReminder_EmailFormat, text)
            : text;
    }

    public static string FormatAmount(long value, CalendarReminderUnit unit)
    {
        var format = unit switch
        {
            CalendarReminderUnit.Weeks => value == 1 ? Translator.CalendarReminder_WeekOption : Translator.CalendarReminder_WeeksOption,
            CalendarReminderUnit.Days => value == 1 ? Translator.CalendarReminder_DayOption : Translator.CalendarReminder_DaysOption,
            CalendarReminderUnit.Hours => value == 1 ? Translator.CalendarReminder_HourOption : Translator.CalendarReminder_HoursOption,
            _ => value == 1 ? Translator.CalendarReminder_MinuteOption : Translator.CalendarReminder_MinutesOption
        };

        return string.Format(format, value);
    }
}
