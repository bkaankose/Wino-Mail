using AppKit;
using Itenso.TimePeriod;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.AppKit.Calendar;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Calendar;

/// <summary>Builds tile models and detail strings from the shared calendar item view model.</summary>
internal static class CalendarTileMapper
{
    public static CalendarTileModel Map(ICalendarItem item, DateOnly date, CalendarSettings? settings)
    {
        var viewModel = item as CalendarItemViewModel;
        var period = new TimeRange(date.ToDateTime(TimeOnly.MinValue), date.AddDays(1).ToDateTime(TimeOnly.MinValue));
        string title = settings is null ? item.Title ?? string.Empty : item.GetDisplayTitle(period, settings) ?? string.Empty;
        string? time = settings is null
            ? $"{item.StartDate:t} – {item.EndDate:t}"
            : $"{settings.GetTimeString(item.StartDate.TimeOfDay)} – {settings.GetTimeString(item.EndDate.TimeOfDay)}";
        return new CalendarTileModel(
            title,
            time,
            viewModel?.Location,
            Fill(item),
            viewModel?.CalendarItem.ShowAs ?? CalendarItemShowAs.Busy,
            item.IsRecurringEvent,
            item.IsMultiDayEvent,
            viewModel?.IsSelected ?? false,
            viewModel?.IsBusy ?? false);
    }

    /// <summary>True when the item belongs to a read-only calendar (subscribed, birthdays, shared without write access).</summary>
    public static bool IsReadOnly(ICalendarItem? item) => item?.AssignedCalendar?.IsReadOnly == true;

    /// <summary>The calendar colour (Windows binds the assigned calendar's background), falling back to the accent.</summary>
    public static NSColor Fill(ICalendarItem item)
    {
        var custom = (item as CalendarItemViewModel)?.CalendarItem.CustomEventColorHex;
        return WinoStyle.FromHexString(custom) ?? WinoStyle.FromHexString(item.AssignedCalendar?.BackgroundColorHex) ?? WinoStyle.Accent;
    }

    /// <summary>Windows CalendarXamlHelpers.GetEventDetailsDateString.</summary>
    public static string DateString(CalendarItemViewModel? item, CalendarSettings? settings)
    {
        if (item is null || settings is null) return string.Empty;
        var start = item.Period.Start;
        var end = item.Period.End;
        var timeFormat = DateTimeDisplayFormatter.GetTimeFormat(settings.DayHeaderDisplayType);
        if (item.IsAllDayEvent)
        {
            var lastDay = end.AddDays(-1);
            return lastDay.Date <= start.Date
                ? $"{start.ToString("dddd, dd MMMM", settings.CultureInfo)} · {Translator.CalendarItemAllDay}"
                : $"{start.ToString("dd MMMM", settings.CultureInfo)} – {lastDay.ToString("dd MMMM", settings.CultureInfo)} · {Translator.CalendarItemAllDay}";
        }
        if (item.IsMultiDayEvent)
            return $"{start.ToString($"dd MMMM ddd {timeFormat}", settings.CultureInfo)} - {end.ToString($"dd MMMM ddd {timeFormat}", settings.CultureInfo)}";
        return $"{start.ToString($"dddd, dd MMMM {timeFormat}", settings.CultureInfo)} - {end.ToString(timeFormat, settings.CultureInfo)}";
    }

    /// <summary>A short recurrence sentence from the RRULE FREQ (the Windows helper uses Ical.Net for the same wording).</summary>
    public static string RecurrenceString(CalendarItemViewModel? item, CalendarSettings? settings)
    {
        var rule = item?.CalendarItem.Recurrence;
        if (item is null || string.IsNullOrWhiteSpace(rule)) return string.Empty;
        var culture = settings?.CultureInfo ?? System.Globalization.CultureInfo.CurrentCulture;
        string? frequency = rule.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .FirstOrDefault(part => part.StartsWith("FREQ=", StringComparison.OrdinalIgnoreCase) || part.Contains(":FREQ=", StringComparison.OrdinalIgnoreCase));
        if (frequency is null) return Translator.CalendarEventCompose_Recurring;
        frequency = frequency[(frequency.IndexOf("FREQ=", StringComparison.OrdinalIgnoreCase) + 5)..].ToUpperInvariant();
        return frequency switch
        {
            "DAILY" => Translator.CalendarEventCompose_RepeatDaily,
            "WEEKLY" => string.Format(Translator.CalendarEventCompose_RepeatWeekly, culture.DateTimeFormat.GetDayName(item.StartDate.DayOfWeek)),
            "MONTHLY" => string.Format(Translator.CalendarEventCompose_RepeatMonthly, item.StartDate.Day),
            "YEARLY" => string.Format(Translator.CalendarEventCompose_RepeatYearly, item.StartDate.ToString("M", culture)),
            _ => Translator.CalendarEventCompose_Recurring
        };
    }

    public static string AttendeeStatusText(AttendeeStatus status) => status switch
    {
        AttendeeStatus.Accepted => Translator.CalendarAttendeeStatus_Accepted,
        AttendeeStatus.Declined => Translator.CalendarAttendeeStatus_Declined,
        AttendeeStatus.Tentative => Translator.CalendarAttendeeStatus_Tentative,
        _ => Translator.CalendarAttendeeStatus_NeedsAction
    };

    public static (WinoIconGlyph Glyph, NSColor Tint) AttendeeStatusGlyph(AttendeeStatus status) => status switch
    {
        AttendeeStatus.Accepted => (WinoIconGlyph.EventAccept, WinoStyle.Success),
        AttendeeStatus.Declined => (WinoIconGlyph.EventDecline, WinoStyle.Critical),
        AttendeeStatus.Tentative => (WinoIconGlyph.EventTentative, WinoStyle.Caution),
        _ => (WinoIconGlyph.EventRespond, WinoStyle.SecondaryText)
    };

    public static (WinoIconGlyph Glyph, NSColor Tint) RsvpGlyph(CalendarItemStatus status) => status switch
    {
        CalendarItemStatus.Accepted => (WinoIconGlyph.EventAccept, WinoStyle.Success),
        CalendarItemStatus.Cancelled => (WinoIconGlyph.EventDecline, WinoStyle.Critical),
        CalendarItemStatus.Tentative => (WinoIconGlyph.EventTentative, WinoStyle.Caution),
        _ => (WinoIconGlyph.EventRespond, WinoStyle.Accent)
    };
}
