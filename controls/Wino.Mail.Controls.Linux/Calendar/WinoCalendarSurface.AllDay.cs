using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Core.Calendar;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Calendar;

/// <summary>
/// The all-day strip: lanes of all-day and multi-day bands above the hour grid, and per day an "n more" link whose
/// popover lists the events that did not fit (macOS WinoCalendarSurfaceView.Overflow).
/// </summary>
public sealed partial class WinoCalendarSurface
{
    private const int OverflowTileWidth = 260;
    private const int OverflowTileHeight = 28;

    private Gtk.Popover? _overflow;
    private readonly List<WinoCalendarTile> _overflowTiles = [];
    private IDisposable? _overflowClosed;
    private string? _allDayLabelText;

    private void DrawAllDay(Cairo.Context cr, int width, int height)
    {
        if (_allDayHeight <= 0 || IsMonth) return;
        var palette = Palette;
        palette.AllDayFill.Use(cr);
        cr.Rectangle(0, 0, width, height);
        cr.Fill();
        palette.Line.Use(cr);
        cr.Rectangle(HourColumnWidth - 1, 0, 1, height);
        cr.Rectangle(0, height - 1, width, 1);
        cr.Fill();
        var label = _layouts.Get(_allDayLabelText ??= Translator.CalendarItemAllDay, CalendarTextStyle.AllDayLabel);
        palette.SecondaryText.Use(cr);
        cr.MoveTo(HourColumnWidth - 8 - label.Width, (height - label.Height) / 2.0);
        PangoCairo.Functions.ShowLayout(cr, label.Layout);
    }

    /// <summary>Weekend shading and day lines of the realized window in rail coordinates, moved by the rail.</summary>
    private void DrawAllDayColumns(Cairo.Context cr, int width, int height)
    {
        if (_allDayHeight <= 0 || IsMonth) return;
        var palette = Palette;
        var dates = _windowDates;
        var dayWidth = DayWidth;
        var visibleCount = VisibleUnitCount;
        cr.Save();
        cr.Translate(-HourColumnWidth, 0);
        palette.WeekendFill.Use(cr);
        for (var index = 0; index < dates.Count; index++)
        {
            if (!WinoCalendarSurfaceGeometry.IsShadedWeekend(dates[index], visibleCount)) continue;
            cr.Rectangle(HourColumnWidth + index * dayWidth, 0, dayWidth, height - 1);
        }
        cr.Fill();
        palette.Line.Use(cr);
        for (var index = 0; index <= dates.Count; index++)
            cr.Rectangle(Math.Round(HourColumnWidth + index * dayWidth) - 1, 0, 1, height);
        cr.Fill();
        cr.Restore();
    }

    private void RelayoutAllDay()
    {
        var (lanes, hasOverflow) = CalendarLayoutAdapter.GetAllDayLaneCount(_windowDates, _items);
        var height = lanes == 0 ? 0 : CalendarLayoutAdapter.GetAllDayHeight(lanes, hasOverflow);
        if (Math.Abs(height - _allDayHeight) > 0.01)
        {
            _allDayHeight = height;
            _allDayArea.SetContentHeight(Math.Max(1, (int)Math.Round(height)));
        }
        _allDayOverlay.SetVisible(!IsMonth && _allDayHeight > 0);
        if (_allDayHeight <= 0)
        {
            _allDayLayer.Clear();
            return;
        }
        var (items, overflows) = CalendarLayoutAdapter.CalculateAllDay(_windowDates, _items, DayWidth, RangeBreakIndex);
        _allDayLayer.Sync(items, Tile, AccessibleNameFactory);
        _allDayLayer.SyncOverflows(overflows, ShowOverflow, _settings?.CultureInfo);
        _allDayArea.QueueDraw();
    }

    private void ShowOverflow(CalendarLayoutOverflowCell cell, Gtk.Button anchor)
    {
        CloseOverflow();
        if (_disposed || cell.HiddenItems.Count == 0) return;
        var list = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        list.SetMarginTop(8);
        list.SetMarginBottom(8);
        list.SetMarginStart(8);
        list.SetMarginEnd(8);
        foreach (var item in cell.HiddenItems)
        {
            var tile = new WinoCalendarTile(_layouts, () => Palette)
            {
                Item = item,
                Kind = CalendarPlacementKind.AllDay,
                Date = cell.Date
            };
            tile.Model = Tile(item, cell.Date);
            tile.SetFrame(new CalendarLayoutRect(0, 0, OverflowTileWidth, OverflowTileHeight));
            tile.SetAccessibleName(AccessibleNameFactory?.Invoke(item, cell.Date) ?? item.Title);
            tile.Clicked += (sender, point) =>
            {
                CloseOverflow();
                Raise(ItemClicked, sender, point);
            };
            tile.DoubleClicked += (sender, point) =>
            {
                CloseOverflow();
                Raise(ItemDoubleClicked, sender, point);
            };
            tile.ContextRequested += (sender, point) => Raise(ItemContextRequested, sender, point);
            OnTileRealized(tile, CalendarStrip.AllDay);
            _overflowTiles.Add(tile);
            list.Append(tile.Root);
        }
        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroller.SetPropagateNaturalHeight(true);
        scroller.SetMaxContentHeight(360);
        scroller.SetChild(list);
        var popover = Gtk.Popover.New();
        popover.SetChild(scroller);
        popover.SetParent(anchor);
        popover.SetPosition(Gtk.PositionType.Bottom);
        popover.SetAutohide(true);
        WinoA11y.Label(popover, CalendarItemOverflow.FormatAccessibleName(cell.HiddenItems.Count, cell.Date, _settings?.CultureInfo));
        _overflow = popover;
        var surface = _ref;
        _overflowClosed = GtkSignals.Connect<Gtk.Popover>(h => popover.OnClosed += h, h => popover.OnClosed -= h, (_, _) =>
            // Unparenting a popover inside its closed handler crashes GTK; it waits one main-loop turn.
            MainLoop.Post(() => surface.Value?.ReleaseOverflow(popover)));
        popover.Popup();
    }

    private void CloseOverflow()
    {
        if (_overflow is { } popover) popover.Popdown();
    }

    private void ReleaseOverflow(Gtk.Popover popover)
    {
        if (!ReferenceEquals(_overflow, popover)) return;
        _overflow = null;
        _overflowClosed?.Dispose();
        _overflowClosed = null;
        foreach (var tile in _overflowTiles)
        {
            OnTileRecycled(tile);
            tile.Release();
        }
        _overflowTiles.Clear();
        popover.Unparent();
    }

    private void DisposeOverflow()
    {
        if (_overflow is { } popover)
        {
            popover.Popdown();
            ReleaseOverflow(popover);
        }
    }

#if DEBUG
    public int OverflowTileCount => _overflowTiles.Count;

    public bool IsOverflowOpen => _overflow is not null;
#endif
}
