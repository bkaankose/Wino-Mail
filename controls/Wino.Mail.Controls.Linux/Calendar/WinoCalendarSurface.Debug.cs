#if DEBUG
using System.Globalization;
using System.Text;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.Controls.Linux.Calendar;

public sealed partial class WinoCalendarSurface
{
    public CalendarFrameLog? FrameLog { get; set; }

    public int DebugShiftCount { get; private set; }

    public double DebugShiftMillisecondsTotal { get; private set; }

    public double DebugShiftMillisecondsMax { get; private set; }

    public void DebugResetShiftCost()
    {
        DebugShiftMillisecondsTotal = 0;
        DebugShiftMillisecondsMax = 0;
    }

    private void DebugRecordShift(double milliseconds)
    {
        DebugShiftMillisecondsTotal += milliseconds;
        DebugShiftMillisecondsMax = Math.Max(DebugShiftMillisecondsMax, milliseconds);
    }

    public void DebugCloseOverflow() => CloseOverflow();

    public IReadOnlyList<DateOnly> DebugDates => _windowDates;

    public int ActiveTileCount => _timedLayer.ActiveCount + _allDayLayer.ActiveCount + _monthLayer.ActiveCount;

    public int SpareTileCount => _timedLayer.SpareCount + _allDayLayer.SpareCount + _monthLayer.SpareCount;

    /// <summary>One line per active tile (strip, title, date, rail frame, allocated position in its strip) and the calculator's frame for the same item.</summary>
    public string DescribeTiles()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"offset={RailOffset:0.#} dayWidth={DayWidth:0.##} hourHeight={HourHeight:0.#} active={ActiveTileCount} spare={SpareTileCount} layouts={_layouts.Count} allDayHeight={_allDayHeight:0.#}").AppendLine();
        var expected = CalendarLayoutAdapter.CalculateTimed(_windowDates, _items, DayWidth, HourHeight, _settings?.EventDisplayMode ?? CalendarEventDisplayMode.Stacked);
        Describe(text, _allDayLayer, _allDayOverlay, null);
        Describe(text, _timedLayer, _timedOverlay, expected);
        Describe(text, _monthLayer, _monthStrip, null);
        text.Append(CultureInfo.InvariantCulture, $"now={NowLineY:0.#} todaySpan={TodaySpan()?.ToString() ?? "-"}").AppendLine();
        return text.ToString();
    }

    /// <summary>The all-day strip or month bars: <c>kind first..last lane title [&lt;][&gt;]</c> (sorted), then the "n more" cells with their hidden titles.</summary>
    public string DescribeBars()
    {
        var month = IsMonth;
        var layer = month ? _monthLayer : _allDayLayer;
        var lines = new List<string>();
        var cellWidth = month ? _monthArea.GetWidth() / (double)Wino.Core.Domain.Models.Calendar.CalendarSurfaceLayoutCalculator.MonthColumns : DayWidth;
        foreach (var tile in layer.Active)
        {
            if (tile.Item is not { } item || cellWidth <= 0) continue;
            var frame = tile.Frame;
            var days = Math.Max(1, (int)Math.Round((frame.Width + 4) / cellWidth));
            var last = tile.Date.AddDays(days - 1);
            var pitch = month ? Wino.Core.Domain.Models.Calendar.CalendarSurfaceLayoutCalculator.MonthItemHeight + Wino.Core.Domain.Models.Calendar.CalendarSurfaceLayoutCalculator.MonthItemGap
                              : Wino.Core.Domain.Models.Calendar.CalendarSurfaceLayoutCalculator.AllDayItemHeight + Wino.Core.Domain.Models.Calendar.CalendarSurfaceLayoutCalculator.AllDayItemGap;
            var top = month ? frame.Y - Math.Floor(frame.Y / Math.Max(1, _unitSize)) * _unitSize - Wino.Core.Domain.Models.Calendar.CalendarSurfaceLayoutCalculator.MonthDayLabelHeight - Wino.Core.Domain.Models.Calendar.CalendarSurfaceLayoutCalculator.MonthCellPadding
                            : frame.Y - Wino.Core.Domain.Models.Calendar.CalendarSurfaceLayoutCalculator.AllDaySectionPadding;
            var lane = (int)Math.Round(top / pitch);
            var before = DateOnly.FromDateTime(item.StartDate) < tile.Date ? " <" : string.Empty;
            var after = item.EndDate > last.AddDays(1).ToDateTime(TimeOnly.MinValue) ? " >" : string.Empty;
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{tile.Kind} {tile.Date:yyyy-MM-dd}..{last:yyyy-MM-dd} lane={lane} '{tile.Model?.Title ?? item.Title}'{before}{after}"));
        }
        lines.Sort(StringComparer.Ordinal);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"strip={(month ? "Month" : "AllDay")} break={RangeBreakIndex} breakDate={(RangeBreakIndex >= 0 && RangeBreakIndex < _windowDates.Count && !month ? _windowDates[RangeBreakIndex].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "-")} bars={lines.Count} allDayHeight={_allDayHeight:0.#}").AppendLine();
        foreach (var line in lines) text.AppendLine(line);
        foreach (var cell in layer.Overflows)
            text.Append(CultureInfo.InvariantCulture, $"more {cell.Date:yyyy-MM-dd} {cell.HiddenItems.Count}: {string.Join(" | ", cell.HiddenItems.Select(static i => i.Title))}").AppendLine();
        return text.ToString();
    }

    private static void Describe(StringBuilder text, CalendarTileLayer layer, Gtk.Widget strip, List<Wino.Core.Domain.Models.Calendar.CalendarLayoutPlacement>? expected)
    {
        foreach (var tile in layer.Active)
        {
            if (tile.Item is not { } item) continue;
            tile.Root.TranslateCoordinates(strip, 0, 0, out var x, out var y);
            var frame = tile.Frame;
            text.Append(CultureInfo.InvariantCulture, $"{layer.Strip} {tile.Kind} '{item.Title}' {tile.Date:yyyy-MM-dd} frame=({frame.X:0.#},{frame.Y:0.#},{frame.Width:0.#},{frame.Height:0.#}) at=({x:0.#},{y:0.#}) visible={tile.Root.GetVisible()} name='{tile.AccessibleName}'");
            if (expected?.Find(p => ReferenceEquals(p.Item, item) && p.Date == tile.Date) is { } match)
                text.Append(CultureInfo.InvariantCulture, $" calc=({match.Bounds.X:0.#},{match.Bounds.Y:0.#},{match.Bounds.Width:0.#},{match.Bounds.Height:0.#})");
            text.AppendLine();
        }
    }
}
#endif
