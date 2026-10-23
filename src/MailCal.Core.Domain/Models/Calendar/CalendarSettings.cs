using System;
using System.Collections.Generic;
using System.Globalization;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;

namespace MailCal.Core.Domain.Models.Calendar;

public record CalendarSettings(DayOfWeek FirstDayOfWeek,
                               List<DayOfWeek> WorkingDays,
                               bool IsWorkingHoursEnabled,
                               DayOfWeek WorkWeekStart,
                               DayOfWeek WorkWeekEnd,
                               TimeSpan WorkingHourStart,
                               TimeSpan WorkingHourEnd,
                               double HourHeight,
                               DayHeaderDisplayType DayHeaderDisplayType,
                               CultureInfo CultureInfo,
                               string TimedDayHeaderDateFormat = "ddd dd",
                               CalendarEventDisplayMode EventDisplayMode = CalendarEventDisplayMode.Stacked)
{
    /// <summary>
    /// Preference names that feed <see cref="IPreferencesService.GetCurrentCalendarSettings"/>.
    /// A change to any of them invalidates a cached <see cref="CalendarSettings"/> snapshot.
    /// </summary>
    public static IReadOnlySet<string> PreferenceKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(IPreferencesService.FirstDayOfWeek),
        nameof(IPreferencesService.WorkingDayStart),
        nameof(IPreferencesService.WorkingDayEnd),
        nameof(IPreferencesService.IsWorkingHoursEnabled),
        nameof(IPreferencesService.WorkingHourStart),
        nameof(IPreferencesService.WorkingHourEnd),
        nameof(IPreferencesService.HourHeight),
        nameof(IPreferencesService.CalendarTimedDayHeaderDateFormat),
        nameof(IPreferencesService.CalendarTimeFormatPreference),
        nameof(IPreferencesService.CalendarEventDisplayMode),
    };

    public int WorkWeekDayCount
    {
        get
        {
            var startOffset = GetWeekOffset(WorkWeekStart);
            var endOffset = GetWeekOffset(WorkWeekEnd);

            if (endOffset < startOffset)
            {
                endOffset += 7;
            }

            return (endOffset - startOffset) + 1;
        }
    }

    public int GetWeekOffset(DayOfWeek dayOfWeek)
        => ((int)dayOfWeek - (int)FirstDayOfWeek + 7) % 7;

    public TimeSpan? GetTimeSpan(string selectedTime)
    {
        // Regardless of the format, we need to parse the time to a TimeSpan.
        // User may list as 14:00 but enters 2:00 PM by input.
        // Be flexible, not annoying.

        if (DateTime.TryParse(selectedTime, CultureInfo, DateTimeStyles.None, out DateTime parsedTime) ||
            DateTime.TryParse(selectedTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedTime) ||
            DateTime.TryParse(selectedTime, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out parsedTime))
        {
            return parsedTime.TimeOfDay;
        }
        else
        {
            return null;
        }
    }

    public string GetTimeString(TimeSpan timeSpan)
    {
        // Here we don't need to be flexible cuz we're saving back the value to the combos.
        // They are populated based on the format and must be returned with the format.

        return DateTimeDisplayFormatter.FormatTime(timeSpan, DayHeaderDisplayType, CultureInfo);
    }

    public string GetTimedDayHeaderText(DateOnly date)
    {
        var format = string.IsNullOrWhiteSpace(TimedDayHeaderDateFormat) ? "ddd dd" : TimedDayHeaderDateFormat;

        try
        {
            return date.ToDateTime(TimeOnly.MinValue).ToString(format, CultureInfo);
        }
        catch (FormatException)
        {
            return date.ToDateTime(TimeOnly.MinValue).ToString("ddd dd", CultureInfo);
        }
    }

    public string GetTimedHourLabelText(int hour)
    {
        if (hour < 0 || hour > 24)
        {
            throw new ArgumentOutOfRangeException(nameof(hour));
        }

        if (DayHeaderDisplayType == DayHeaderDisplayType.TwentyFourHour)
        {
            return hour.ToString(CultureInfo);
        }

        var displayHour = hour % 24;
        var dateTime = DateTime.Today.AddHours(displayHour);
        return dateTime.ToString("h tt", CultureInfo);
    }
}
