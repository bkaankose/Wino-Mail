using Itenso.TimePeriod;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Linux.Calendar;
using Wino.Mail.ViewModels.Calendar;
using Wino.Mail.ViewModels.Calendar.Data;
using Wino.Presentation;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>Builds tile models and accessible names from the shared calendar item ViewModel (macOS CalendarTileMapper, Windows CalendarXamlHelpers).</summary>
internal static class CalendarTileMapper
{
    public static CalendarTileModel Map(ICalendarItem item, DateOnly date, CalendarSettings? settings)
    {
        var viewModel = item as CalendarItemViewModel;
        var period = new TimeRange(date.ToDateTime(TimeOnly.MinValue), date.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var title = settings is null ? item.Title ?? string.Empty : item.GetDisplayTitle(period, settings) ?? string.Empty;
        return new CalendarTileModel(
            title,
            CalendarAgenda.TimeRangeText(item.StartDate, item.EndDate, settings),
            viewModel?.Location,
            Fill(item),
            viewModel?.CalendarItem.ShowAs ?? CalendarItemShowAs.Busy,
            item.IsRecurringEvent,
            item.IsMultiDayEvent,
            viewModel?.IsSelected ?? false,
            viewModel?.IsBusy ?? false,
            item.IsAllDayEvent,
            viewModel?.IsCancelled ?? false,
            viewModel?.NeedsResponse ?? false,
            viewModel?.IsPast ?? false);
    }

    public static string AccessibleName(ICalendarItem item, DateOnly date, CalendarSettings? settings)
    {
        var viewModel = item as CalendarItemViewModel;
        var parts = new List<string>(6) { item.Title ?? string.Empty };
        if (settings is not null) parts.Add(CalendarAccessibilityFormatter.FormatPeriod(item.StartDate, item.EndDate, item.IsAllDayEvent, settings));
        if (item.IsRecurringEvent) parts.Add(Translator.CalendarEventCompose_Recurring);
        if (viewModel?.StateDescription is { Length: > 0 } state) parts.Add(state);
        if (viewModel?.Location is { Length: > 0 } location) parts.Add(location);
        if (item.AssignedCalendar?.Name is { Length: > 0 } calendar) parts.Add(calendar);
        return string.Join(", ", parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    public static WinoRgba Fill(ICalendarItem item)
    {
        var hex = item is CalendarItemViewModel viewModel ? CalendarAgenda.ColorHex(viewModel) : item.AssignedCalendar?.BackgroundColorHex;
        if (WinoRgba.FromHexString(hex) is { } color) return color;
        if (WinoAppearance.Accent is { } accent) return accent;
        var rgba = Adw.StyleManager.GetDefault().GetAccentColorRgba();
        return new WinoRgba((byte)(rgba.Red * 255), (byte)(rgba.Green * 255), (byte)(rgba.Blue * 255));
    }

    public static bool IsReadOnly(ICalendarItem? item) => CalendarEventDisplay.IsReadOnly(item);

    /// <summary>Why a tile cannot be dragged (shared <see cref="CalendarEventDisplay.GetDragRefusal"/>).</summary>
    public static CalendarDragRefusal DragRefusal(ICalendarItem item)
        => item is CalendarItemViewModel viewModel
            ? CalendarEventDisplay.GetDragRefusal(viewModel.CalendarItem, viewModel.IsBusy)
            : CalendarDragRefusal.Busy;
}
