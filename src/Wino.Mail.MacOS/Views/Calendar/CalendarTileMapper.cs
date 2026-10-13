using AppKit;
using Itenso.TimePeriod;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.AppKit.Calendar;
using Wino.Presentation.AppKit;
using Wino.Core.Domain.Presentation;

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

    /// <summary>True when the item belongs to a read-only calendar (shared <see cref="CalendarEventDisplay.IsReadOnly"/>).</summary>
    public static bool IsReadOnly(ICalendarItem? item) => CalendarEventDisplay.IsReadOnly(item);

    /// <summary>Whether a tile may be dragged to move or resize it (Windows CalendarItemViewModel.CanDragDrop, plus the read-only calendar rule).</summary>
    public static bool CanDrag(ICalendarItem item) => DragRefusal(item) == CalendarDragRefusal.None;

    /// <summary>Why a tile cannot be dragged (shared <see cref="CalendarEventDisplay.GetDragRefusal"/>).</summary>
    public static CalendarDragRefusal DragRefusal(ICalendarItem item)
        => item is CalendarItemViewModel viewModel
            ? CalendarEventDisplay.GetDragRefusal(viewModel.CalendarItem, viewModel.IsBusy)
            : CalendarDragRefusal.Busy;

    /// <summary>The calendar colour (Windows binds the assigned calendar's background), falling back to the accent.</summary>
    public static NSColor Fill(ICalendarItem item)
    {
        var custom = (item as CalendarItemViewModel)?.CalendarItem.CustomEventColorHex;
        return WinoStyle.FromHexString(custom) ?? WinoStyle.FromHexString(item.AssignedCalendar?.BackgroundColorHex) ?? WinoStyle.Accent;
    }

    /// <summary>The event details date line (shared <see cref="CalendarEventDisplay.FormatDateRange"/>).</summary>
    public static string DateString(CalendarItemViewModel? item, CalendarSettings? settings)
        => item is null ? string.Empty : CalendarEventDisplay.FormatDateRange(item.Period.Start, item.Period.End, item.IsAllDayEvent, item.IsMultiDayEvent, settings);

    /// <summary>The recurrence sentence (shared <see cref="CalendarEventDisplay.FormatRecurrence"/>).</summary>
    public static string RecurrenceString(CalendarItemViewModel? item, CalendarSettings? settings)
        => item is null ? string.Empty : CalendarEventDisplay.FormatRecurrence(item.CalendarItem.Recurrence, item.Period.Start, item.Period.End, item.IsAllDayEvent, settings);

    public static string AttendeeStatusText(AttendeeStatus status) => CalendarEventDisplay.GetAttendeeStatusText(status);

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
