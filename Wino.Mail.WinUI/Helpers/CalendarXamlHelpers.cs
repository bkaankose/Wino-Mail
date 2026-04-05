using System;
using System.Linq;
using System.Text.RegularExpressions;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
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

        string timeFormat = settings.DayHeaderDisplayType == DayHeaderDisplayType.TwelveHour ? "h:mm tt" : "HH:mm";
        string dateFormat = settings.DayHeaderDisplayType == DayHeaderDisplayType.TwelveHour ? "dddd, dd MMMM h:mm tt" : "dddd, dd MMMM HH:mm";

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

        var calendarEvent = new CalendarEvent
        {
            Start = new CalDateTime(calendarItemViewModel.StartDate),
            End = new CalDateTime(calendarItemViewModel.EndDate),
        };

        var recurrenceLines = Regex.Split(calendarItemViewModel.CalendarItem.Recurrence, Constants.CalendarEventRecurrenceRuleSeperator);
        foreach (var line in recurrenceLines.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            calendarEvent.RecurrenceRules.Add(new RecurrencePattern(line));
        }

        var recurrenceRule = calendarEvent.RecurrenceRules.FirstOrDefault();
        if (recurrenceRule == null)
        {
            return string.Empty;
        }

        var frequency = MapFrequency(recurrenceRule.Frequency.ToString());
        if (!frequency.HasValue)
        {
            return string.Empty;
        }

        return CalendarRecurrenceSummaryFormatter.BuildSummary(
            isRecurring: true,
            effectiveStart: calendarItemViewModel.Period.Start,
            effectiveEnd: calendarItemViewModel.Period.End,
            isAllDay: calendarItemViewModel.IsAllDayEvent,
            settings: settings,
            interval: recurrenceRule.Interval <= 0 ? 1 : recurrenceRule.Interval,
            frequency: frequency.Value,
            daysOfWeek: recurrenceRule.ByDay?.Select(day => day.DayOfWeek).ToList() ?? [],
            recurrenceEndDate: recurrenceRule.Until == default ? null : new DateTimeOffset(recurrenceRule.Until));
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

