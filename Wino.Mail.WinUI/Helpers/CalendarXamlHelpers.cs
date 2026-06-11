using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Controls.Primitives;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Collections;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Helpers;

namespace Wino.Calendar.Helpers;

public static class CalendarXamlHelpers
{
    public static CalendarItemViewModel GetFirstAllDayEvent(CalendarEventCollection collection)
        => collection.AllDayEvents.OfType<CalendarItemViewModel>().FirstOrDefault()!;

    public static string GetEventDetailsDateString(CalendarItemViewModel calendarItemViewModel, CalendarSettings settings)
    {
        if (calendarItemViewModel == null || settings == null) return string.Empty;

        var start = calendarItemViewModel.Period.Start;
        var end = calendarItemViewModel.Period.End;

        var timeFormat = DateTimeDisplayFormatter.GetTimeFormat(settings.DayHeaderDisplayType);
        var dateFormat = $"dddd, dd MMMM {timeFormat}";

        if (calendarItemViewModel.IsMultiDayEvent)
        {
            return $"{start.ToString($"dd MMMM ddd {timeFormat}", settings.CultureInfo)} - {end.ToString($"dd MMMM ddd {timeFormat}", settings.CultureInfo)}";
        }

        return $"{start.ToString(dateFormat, settings.CultureInfo)} - {end.ToString(timeFormat, settings.CultureInfo)}";
    }

    public static string GetRecurrenceString(CalendarItemViewModel calendarItemViewModel, CalendarSettings settings)
    {
        if (calendarItemViewModel == null || string.IsNullOrEmpty(calendarItemViewModel.CalendarItem.Recurrence))
        {
            return string.Empty;
        }

        // Display-only RRULE parsing (FREQ/INTERVAL/BYDAY/UNTIL of the first rule line),
        // done by hand so the UI process does not need Ical.Net.
        var recurrenceLines = Regex.Split(calendarItemViewModel.CalendarItem.Recurrence, Constants.CalendarEventRecurrenceRuleSeperator);
        var ruleLine = recurrenceLines.FirstOrDefault(line => line.Contains("FREQ=", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(ruleLine))
        {
            return string.Empty;
        }

        var ruleParts = ParseRecurrenceRuleParts(ruleLine);

        if (!ruleParts.TryGetValue("FREQ", out var frequencyValue))
        {
            return string.Empty;
        }

        var frequency = MapFrequency(frequencyValue);
        if (!frequency.HasValue)
        {
            return string.Empty;
        }

        int interval = 1;
        if (ruleParts.TryGetValue("INTERVAL", out var intervalValue) && int.TryParse(intervalValue, out var parsedInterval) && parsedInterval > 0)
        {
            interval = parsedInterval;
        }

        var daysOfWeek = ruleParts.TryGetValue("BYDAY", out var byDayValue)
            ? ParseByDay(byDayValue)
            : [];

        DateTimeOffset? recurrenceEndDate = ruleParts.TryGetValue("UNTIL", out var untilValue)
            ? ParseUntil(untilValue)
            : null;

        return CalendarRecurrenceSummaryFormatter.BuildSummary(
            isRecurring: true,
            effectiveStart: calendarItemViewModel.Period.Start,
            effectiveEnd: calendarItemViewModel.Period.End,
            isAllDay: calendarItemViewModel.IsAllDayEvent,
            settings: settings,
            interval: interval,
            frequency: frequency.Value,
            daysOfWeek: daysOfWeek,
            recurrenceEndDate: recurrenceEndDate);
    }

    private static Dictionary<string, string> ParseRecurrenceRuleParts(string ruleLine)
    {
        // Strip a leading "RRULE:" (or any property name before ':').
        var colonIndex = ruleLine.IndexOf(':');
        if (colonIndex >= 0 && !ruleLine.AsSpan(0, colonIndex).Contains('='))
        {
            ruleLine = ruleLine[(colonIndex + 1)..];
        }

        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in ruleLine.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = part.IndexOf('=');
            if (separatorIndex <= 0) continue;

            parts[part[..separatorIndex].Trim().ToUpperInvariant()] = part[(separatorIndex + 1)..].Trim();
        }

        return parts;
    }

    private static List<DayOfWeek> ParseByDay(string byDayValue)
    {
        var days = new List<DayOfWeek>();

        foreach (var token in byDayValue.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            // Tokens may carry an ordinal prefix like "1MO" or "-1SU"; the day code is the last two chars.
            var dayCode = token.Trim();
            if (dayCode.Length < 2) continue;

            DayOfWeek? day = dayCode[^2..].ToUpperInvariant() switch
            {
                "SU" => DayOfWeek.Sunday,
                "MO" => DayOfWeek.Monday,
                "TU" => DayOfWeek.Tuesday,
                "WE" => DayOfWeek.Wednesday,
                "TH" => DayOfWeek.Thursday,
                "FR" => DayOfWeek.Friday,
                "SA" => DayOfWeek.Saturday,
                _ => null
            };

            if (day.HasValue)
            {
                days.Add(day.Value);
            }
        }

        return days;
    }

    private static DateTimeOffset? ParseUntil(string untilValue)
    {
        string[] formats = ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd"];

        if (DateTime.TryParseExact(untilValue, formats, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var until))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(until, DateTimeKind.Utc));
        }

        return null;
    }

    public static string GetDetailsPopupDurationString(CalendarItemViewModel calendarItemViewModel, CalendarSettings settings)
    {
        if (calendarItemViewModel == null || settings == null) return string.Empty;

        if (!calendarItemViewModel.IsAllDayEvent && !calendarItemViewModel.IsMultiDayEvent)
        {
            return $"{calendarItemViewModel.Period.Start.ToString("d", settings.CultureInfo)} {settings.GetTimeString(calendarItemViewModel.Period.Duration)}";
        }

        if (calendarItemViewModel.IsMultiDayEvent)
        {
            return $"{calendarItemViewModel.Period.Start.ToString("d", settings.CultureInfo)} - {calendarItemViewModel.Period.End.ToString("d", settings.CultureInfo)}";
        }

        return $"{calendarItemViewModel.Period.Start.ToString("d", settings.CultureInfo)} ({Translator.CalendarItemAllDay})";
    }

    public static PopupPlacementMode GetDesiredPlacementModeForEventsDetailsPopup(
        CalendarItemViewModel calendarItemViewModel,
        CalendarDisplayType calendarDisplayType)
    {
        if (calendarItemViewModel == null) return PopupPlacementMode.Auto;

        if (calendarItemViewModel.IsAllDayEvent || calendarItemViewModel.IsMultiDayEvent) return PopupPlacementMode.Bottom;

        return XamlHelpers.GetPlaccementModeForCalendarType(calendarDisplayType);
    }

    public static bool HasOnlineMeetingLink(CalendarItemViewModel calendarItemViewModel)
        => calendarItemViewModel != null && !string.IsNullOrEmpty(calendarItemViewModel.CalendarItem?.HtmlLink);

    public static string GetAttendeeStatusText(AttendeeStatus status)
    {
        return status switch
        {
            AttendeeStatus.Accepted => Translator.CalendarAttendeeStatus_Accepted,
            AttendeeStatus.Declined => Translator.CalendarAttendeeStatus_Declined,
            AttendeeStatus.Tentative => Translator.CalendarAttendeeStatus_Tentative,
            AttendeeStatus.NeedsAction => Translator.CalendarAttendeeStatus_NeedsAction,
            _ => string.Empty
        };
    }

    public static Microsoft.UI.Xaml.Visibility GetAttendeeStatusVisibility(AttendeeStatus status)
    {
        return Microsoft.UI.Xaml.Visibility.Visible;
    }

    private static CalendarItemRecurrenceFrequency? MapFrequency(string frequency)
    {
        return frequency.ToUpperInvariant() switch
        {
            "DAILY" => CalendarItemRecurrenceFrequency.Daily,
            "WEEKLY" => CalendarItemRecurrenceFrequency.Weekly,
            "MONTHLY" => CalendarItemRecurrenceFrequency.Monthly,
            "YEARLY" => CalendarItemRecurrenceFrequency.Yearly,
            _ => null
        };
    }
}

