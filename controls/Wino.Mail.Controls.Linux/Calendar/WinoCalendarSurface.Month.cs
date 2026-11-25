using System.Globalization;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Core.Calendar;
using Wino.Mail.Controls.Core.Layout;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Calendar;

/// <summary>
/// The month grid (macOS MonthRailView): week rows on the vertical rail under a weekday header, day cells with the day
/// number (today on an accent disc, days outside the dominant month dimmed), weekend shading, title entries and bands,
/// and an "n more" link per day whose popover lists the hidden events. The grid of the whole realized window is drawn
/// once per layout into a drawing area that moves with the tiles, so a frame of motion redraws nothing.
/// </summary>
public sealed partial class WinoCalendarSurface
{
    private const double DimmedLabelOpacity = 0.45;

    private readonly Gtk.Overlay _monthStrip = Gtk.Overlay.New();
    private readonly Gtk.DrawingArea _monthArea = Gtk.DrawingArea.New();
    private readonly Gtk.DrawingArea _monthGrid = Gtk.DrawingArea.New();
    private readonly CalendarTileLayer _monthLayer;
    private List<CalendarLayoutMonthCell> _monthCells = [];
    private double _monthLaidOutWidth;
    private string[]? _dayNumbers;
    private CultureInfo? _dayNumbersCulture;

    private void BuildMonth()
    {
        _monthArea.SetContentWidth(1);
        _monthArea.SetContentHeight(1);
        _monthArea.SetHexpand(true);
        _monthArea.SetVexpand(true);
        _monthStrip.SetChild(_monthArea);
        _monthStrip.SetOverflow(Gtk.Overflow.Hidden);
        _monthStrip.AddOverlay(_monthLayer.Fixed);
        _monthGrid.SetCanTarget(false);
        _monthGrid.SetContentWidth(1);
        _monthGrid.SetContentHeight(1);
        _monthLayer.Content.Put(_monthGrid, 0, 0);
        var surface = _ref;
        _monthGrid.SetDrawFunc((_, cr, width, height) => surface.Value?.DrawMonth(cr, width, height));
        var area = _monthArea;
        // The draw function's closure must not reach the surface itself: GTK keeps it until the area is destroyed.
        Own(GtkSignals.Connect<Gtk.DrawingArea, Gtk.DrawingArea.ResizeSignalArgs>(h => area.OnResize += h, h => area.OnResize -= h, (_, _) => surface.Value?.RequestRelayout()));
    }

    /// <summary>The month branch of a layout pass: the unit is a week row, a sixth of the strip.</summary>
    private void LayoutMonth()
    {
        _headerArea.SetContentHeight((int)WinoCalendarSurfaceGeometry.MonthHeaderHeight);
        SetUnitSize(_monthArea.GetHeight() / (double)Math.Max(1, VisibleUnitCount));
        // Month units are week rows, so a width-only change (the details zone opening) keeps the unit size.
        if (Math.Abs(_monthArea.GetWidth() - _monthLaidOutWidth) > 0.5) _railDirty = true;
    }

    private void RelayoutMonth()
    {
        var layer = _monthLayer;
        var width = _monthArea.GetWidth();
        if (_windowDates.Count == 0 || width <= 0 || _unitSize <= 0)
        {
            _monthCells = [];
            layer.Clear();
            _monthGrid.QueueDraw();
            return;
        }
        var (cells, items, overflows) = CalendarLayoutAdapter.CalculateMonth(_windowDates, _items, width, _unitSize * CalendarSurfaceLayoutCalculator.MonthRows);
        _monthCells = cells;
        layer.Sync(items, Tile, AccessibleNameFactory);
        layer.SyncOverflows(overflows, ShowOverflow, _settings?.CultureInfo);
        _monthLaidOutWidth = width;
        var rows = (cells.Count + CalendarSurfaceLayoutCalculator.MonthColumns - 1) / CalendarSurfaceLayoutCalculator.MonthColumns;
        _monthGrid.SetContentWidth(width);
        _monthGrid.SetContentHeight(Math.Max(1, (int)Math.Ceiling(rows * _unitSize)));
        _monthGrid.QueueDraw();
    }

    private void DrawMonth(Cairo.Context cr, int width, int height)
    {
        var cells = _monthCells;
        if (cells.Count == 0 || _unitSize <= 0) return;
        var palette = Palette;
        var dominant = _dominantMonth;
        var today = Today();
        var numbers = DayNumbers();
        for (var index = 0; index < cells.Count; index++)
        {
            var cell = cells[index];
            var bounds = cell.Bounds;
            if (cell.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                palette.WeekendFill.Use(cr);
                cr.Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                cr.Fill();
            }
            var label = numbers[cell.Date.Day];
            if (cell.Date == today)
            {
                palette.Accent.WithAlpha(0.10).Use(cr);
                cr.Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                cr.Fill();
                palette.Accent.Use(cr);
                cr.Arc(bounds.X + 3 + 10, bounds.Y + 2 + 10, 10, 0, 2 * Math.PI);
                cr.Fill();
                var text = _layouts.Get(label, CalendarTextStyle.MonthDayToday);
                palette.AccentForeground.Use(cr);
                cr.MoveTo(bounds.X + 3 + (20 - text.Width) / 2.0, bounds.Y + 2 + (20 - text.Height) / 2.0);
                PangoCairo.Functions.ShowLayout(cr, text.Layout);
            }
            else
            {
                var text = _layouts.Get(label, CalendarTextStyle.MonthDay);
                var inMonth = cell.Date.Month == dominant.Month && cell.Date.Year == dominant.Year;
                (inMonth ? palette.Text : palette.Text.WithAlpha(DimmedLabelOpacity)).Use(cr);
                cr.MoveTo(bounds.X + 6, bounds.Y + 3);
                PangoCairo.Functions.ShowLayout(cr, text.Layout);
            }
        }

        var cellWidth = width / (double)CalendarSurfaceLayoutCalculator.MonthColumns;
        var rows = (cells.Count + CalendarSurfaceLayoutCalculator.MonthColumns - 1) / CalendarSurfaceLayoutCalculator.MonthColumns;
        palette.Line.Use(cr);
        for (var row = 0; row <= rows; row++) cr.Rectangle(0, Math.Round(row * _unitSize) - 1, width, 1);
        for (var column = 1; column < CalendarSurfaceLayoutCalculator.MonthColumns; column++)
            cr.Rectangle(Math.Round(column * cellWidth), 0, 1, height);
        cr.Fill();
        DrawSelection(cr, CalendarStrip.Month);
    }

    private void DrawMonthHeader(Cairo.Context cr, int width, int height)
    {
        var culture = _settings?.CultureInfo ?? CultureInfo.CurrentCulture;
        var first = _settings?.FirstDayOfWeek ?? DayOfWeek.Monday;
        var cellWidth = width / (double)CalendarSurfaceLayoutCalculator.MonthColumns;
        Palette.SecondaryText.Use(cr);
        for (var column = 0; column < CalendarSurfaceLayoutCalculator.MonthColumns; column++)
        {
            var name = culture.DateTimeFormat.AbbreviatedDayNames[((int)first + column) % 7];
            var text = _layouts.Get(name, CalendarTextStyle.MonthWeekday);
            cr.MoveTo(column * cellWidth + (cellWidth - text.Width) / 2, (height - text.Height) / 2.0);
            PangoCairo.Functions.ShowLayout(cr, text.Layout);
        }
    }

    private string[] DayNumbers()
    {
        var culture = _settings?.CultureInfo ?? CultureInfo.CurrentCulture;
        if (_dayNumbers is { } numbers && ReferenceEquals(culture, _dayNumbersCulture)) return numbers;
        numbers = new string[32];
        for (var day = 1; day <= 31; day++) numbers[day] = day.ToString(culture);
        _dayNumbers = numbers;
        _dayNumbersCulture = culture;
        return numbers;
    }

    /// <summary>The day under a point in rail coordinates; with <paramref name="clamp"/>, the nearest visible day when the point lies outside.</summary>
    internal DateTime? MonthCellAt(double railX, double railY, bool clamp = false)
    {
        if (_monthCells.Count == 0) return null;
        if (clamp)
        {
            var offset = RailOffset;
            railX = Math.Clamp(railX, 0, Math.Max(0, _monthArea.GetWidth() - 1));
            railY = Math.Clamp(railY, offset, offset + Math.Max(0, _monthArea.GetHeight() - 1));
        }
        foreach (var cell in _monthCells)
        {
            var bounds = cell.Bounds;
            if (railX >= bounds.X && railX < bounds.X + bounds.Width && railY >= bounds.Y && railY < bounds.Y + bounds.Height)
                return cell.Date.ToDateTime(TimeOnly.MinValue);
        }
        return null;
    }

    /// <summary>The cell of a realized day in rail coordinates.</summary>
    internal CalendarLayoutRect? MonthCellBounds(DateOnly date)
    {
        foreach (var cell in _monthCells) if (cell.Date == date) return cell.Bounds;
        return null;
    }

    partial void ResolveMonthPress(double railX, double railY, ref DateTime? cell, ref Action? click)
    {
        if (MonthCellAt(railX, railY) is not { } day || MonthCellBounds(DateOnly.FromDateTime(day)) is not { } bounds) return;
        cell = day;
        var frame = new WinoLayoutFrame(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        click = () => RaiseSlotClicked(day, frame, CalendarStrip.Month, isAllDay: true);
    }

#if DEBUG
    public string DescribeMonth()
        => string.Create(CultureInfo.InvariantCulture,
            $"dominant={_dominantMonth:yyyy-MM} cells={_monthCells.Count} first={(_monthCells.Count > 0 ? _monthCells[0].Date : default):yyyy-MM-dd} last={(_monthCells.Count > 0 ? _monthCells[^1].Date : default):yyyy-MM-dd} unit={_unitSize:0.#} offset={RailOffset:0.#} tiles={_monthLayer.ActiveCount} more={_monthLayer.OverflowCount}");
#endif
}
