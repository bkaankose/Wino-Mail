using Android.Content;
using Android.Graphics;
using Itenso.TimePeriod;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Android.Calendar;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Calendar;

/// <summary>
/// Tile models and colours from the shared calendar item view model (Android twin of the UIKit and Mac
/// CalendarTileMapper); the texts and colour choice are the shared <see cref="CalendarAgenda"/> rules.
/// </summary>
internal static class CalendarTiles
{
    /// <summary>The event colour (its own, else its calendar's), falling back to the accent.</summary>
    public static Color Fill(Context context, CalendarItemViewModel item)
        => WinoColors.FromHexString(CalendarAgenda.ColorHex(item)) ?? WinoColors.Accent(context);

    public static WinoCalendarTileModel Map(Context context, ICalendarItem item, DateOnly date, CalendarSettings? settings)
    {
        if (item is not CalendarItemViewModel viewModel)
            return new WinoCalendarTileModel(item.Title ?? string.Empty, null, WinoColors.Accent(context), CalendarItemShowAs.Busy, false);
        var period = new TimeRange(date.ToDateTime(TimeOnly.MinValue), date.AddDays(1).ToDateTime(TimeOnly.MinValue));
        string title = settings is null ? viewModel.Title ?? string.Empty : viewModel.GetDisplayTitle(period, settings) ?? string.Empty;
        string time = CalendarAgenda.TimeRangeText(viewModel.StartDate, viewModel.EndDate, settings);
        string detail = string.IsNullOrWhiteSpace(viewModel.Location) ? time : $"{time} · {viewModel.Location}";
        return new WinoCalendarTileModel(title, detail, Fill(context, viewModel), viewModel.CalendarItem.ShowAs, viewModel.IsSelected);
    }
}
