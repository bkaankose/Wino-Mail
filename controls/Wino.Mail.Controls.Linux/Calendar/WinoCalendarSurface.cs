using System.Collections.Specialized;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Core.Calendar;
using Wino.Presentation;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Calendar;

/// <summary>
/// The calendar period surface (Windows CalendarPeriodControl, macOS WinoCalendarSurfaceView): a 44 px day header with
/// today on an accent pill, the all-day strip, and a 64 px hour gutter beside a scrolling 30-minute grid with weekend
/// and working-hour shading, the current-time line and pooled event tiles. Each strip is one Cairo drawing area with a
/// tile layer over it; the day columns sit on the rail (<c>.Rail.cs</c>, docs/calendar-rail-navigation.md), so a move
/// along it only redraws the strips and moves the tiles.
/// </summary>
public sealed partial class WinoCalendarSurface : IDisposable
{
    internal const double HourColumnWidth = WinoCalendarSurfaceGeometry.HourColumnWidth;
    internal const double TimedHeaderHeight = WinoCalendarSurfaceGeometry.TimedHeaderHeight;
    internal const double TimelinePadding = WinoCalendarSurfaceGeometry.TimelinePadding;

    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Gtk.Overlay _headerOverlay = Gtk.Overlay.New();
    private readonly Gtk.DrawingArea _headerArea = Gtk.DrawingArea.New();
    private readonly Gtk.DrawingArea _headerColumns = Gtk.DrawingArea.New();
    private readonly Gtk.DrawingArea _allDayColumns = Gtk.DrawingArea.New();
    private readonly Gtk.Overlay _allDayOverlay = Gtk.Overlay.New();
    private readonly Gtk.DrawingArea _allDayArea = Gtk.DrawingArea.New();
    private readonly Gtk.Stack _stack = Gtk.Stack.New();
    private readonly Gtk.ScrolledWindow _scroller = Gtk.ScrolledWindow.New();
    private readonly Gtk.Overlay _timedOverlay = Gtk.Overlay.New();
    private readonly Gtk.DrawingArea _timedArea = Gtk.DrawingArea.New();
    private readonly CalendarLayoutCache _layouts;
    private readonly CalendarTileLayer _headerLayer;
    private readonly CalendarTileLayer _allDayLayer;
    private readonly CalendarTileLayer _timedLayer;
    private readonly SurfaceRef _ref;
    private readonly List<IDisposable> _owned = [];
    private readonly NotifyCollectionChangedEventHandler _itemsChanged;
    private VisibleDateRange? _range;
    private CalendarSettings? _settings;
    private IReadOnlyList<ICalendarItem> _items = (ICalendarItem[])[];
    private INotifyCollectionChanged? _observable;
    private bool _scrolledToStart;
    private CalendarDisplayType? _lastDisplayType;
    private double _allDayHeight;
    private uint _clock;
    private uint _relayoutTick;
    private bool _relayoutRequested;
    private bool _inRelayoutTick;
#if DEBUG
    private int _layoutCount;
#endif
    private bool _disposed;

    public WinoCalendarSurface()
    {
        WinoLiveObjects.Track(this);
        _ref = new SurfaceRef { Value = this };
        _layouts = new CalendarLayoutCache(_root);
        _root.AddCssClass("wino-calendar-surface");
        _root.SetFocusable(true);
        _root.SetHexpand(true);
        _root.SetVexpand(true);

        _headerArea.SetContentHeight((int)TimedHeaderHeight);
        _headerArea.SetContentWidth(1);
        _headerArea.SetHexpand(true);
        _headerOverlay.SetChild(_headerArea);
        _headerLayer = new CalendarTileLayer(CalendarStrip.Header, HourColumnWidth, () => CreateTile(CalendarStrip.Header), TileRecycled);
        _headerOverlay.AddOverlay(_headerLayer.Fixed);
        PutRailContent(_headerLayer, _headerColumns);

        _allDayArea.SetContentHeight(1);
        _allDayArea.SetContentWidth(1);
        _allDayOverlay.SetChild(_allDayArea);
        _allDayLayer = new CalendarTileLayer(CalendarStrip.AllDay, HourColumnWidth, () => CreateTile(CalendarStrip.AllDay), TileRecycled);
        _allDayOverlay.AddOverlay(_allDayLayer.Fixed);
        PutRailContent(_allDayLayer, _allDayColumns);
        _allDayOverlay.SetVisible(false);

        _timedArea.SetContentWidth(1);
        _timedArea.SetContentHeight((int)WinoCalendarSurfaceGeometry.CanvasHeight(HourHeight));
        _timedOverlay.SetChild(_timedArea);
        _timedLayer = new CalendarTileLayer(CalendarStrip.Timed, HourColumnWidth, () => CreateTile(CalendarStrip.Timed), TileRecycled);
        _timedOverlay.AddOverlay(_timedLayer.Fixed);
        _scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        _scroller.SetOverlayScrolling(true);
        _scroller.SetPropagateNaturalHeight(false);
        _scroller.SetVexpand(true);
        _scroller.SetChild(_timedOverlay);

        _stack.AddNamed(_scroller, "timed");
        _monthLayer = new CalendarTileLayer(CalendarStrip.Month, 0, () => CreateTile(CalendarStrip.Month), TileRecycled);
        BuildMonth();
        _stack.AddNamed(_monthStrip, "month");
        _stack.SetVisibleChildName("timed");
        _stack.SetVexpand(true);

        _root.Append(_headerOverlay);
        _root.Append(_allDayOverlay);
        _root.Append(_stack);

        var surface = _ref;
        _headerArea.SetDrawFunc((_, cr, width, height) => surface.Value?.DrawHeader(cr, width, height));
        _allDayArea.SetDrawFunc((_, cr, width, height) => surface.Value?.DrawAllDay(cr, width, height));
        _timedArea.SetDrawFunc((_, cr, width, height) => surface.Value?.DrawTimed(cr, width, height));
        _headerColumns.SetDrawFunc((_, cr, width, height) => surface.Value?.DrawHeaderColumns(cr, width, height));
        _allDayColumns.SetDrawFunc((_, cr, width, height) => surface.Value?.DrawAllDayColumns(cr, width, height));

        var timed = _timedArea;
        Own(GtkSignals.Connect<Gtk.DrawingArea, Gtk.DrawingArea.ResizeSignalArgs>(h => timed.OnResize += h, h => timed.OnResize -= h, (_, _) => RequestRelayout()));
        var root = _root;
        Own(GtkSignals.Connect<Gtk.Widget>(h => root.OnMap += h, h => root.OnMap -= h, (_, _) => OnMapped()));
        Own(GtkSignals.Connect<Gtk.Widget>(h => root.OnUnmap += h, h => root.OnUnmap -= h, (_, _) =>
        {
            StopClock();
            OnUnmapped();
        }));
        Own(GtkSignals.OnNotify(_root, "scale-factor", () =>
        {
            _layouts.Clear();
            Invalidate();
        }));
        _itemsChanged = OnItemsChanged;
        SubscribePalette();
        AttachKeys();
        AttachInput();
        AttachSlotMenu();
    }

    public Gtk.Widget Root => _root;

    public Func<ICalendarItem, DateOnly, CalendarTileModel>? TileFactory { get; set; }

    public Func<ICalendarItem, DateOnly, string>? AccessibleNameFactory { get; set; }

    public Func<DateOnly> Today { get; set; } = static () => DateOnly.FromDateTime(DateTime.Now);

    public VisibleDateRange? VisibleRange
    {
        get => _range;
        set
        {
            if (_disposed) return;
            _range = value;
            if (value?.DisplayType != _lastDisplayType)
            {
                _scrolledToStart = false;
                _lastDisplayType = value?.DisplayType;
            }
            ApplyRange(fullRelayout: false);
        }
    }

    public CalendarSettings? Settings
    {
        get => _settings;
        set
        {
            if (_disposed) return;
            _settings = value;
            _hourLabels = null;
            _timedArea.SetContentHeight((int)WinoCalendarSurfaceGeometry.CanvasHeight(HourHeight));
            ApplyRange(fullRelayout: true);
        }
    }

    public IReadOnlyList<ICalendarItem> Items
    {
        get => _items;
        set
        {
            if (_disposed) return;
            if (_observable is not null) _observable.CollectionChanged -= _itemsChanged;
            _items = value ?? (ICalendarItem[])[];
            _observable = value as INotifyCollectionChanged;
            if (_observable is not null) _observable.CollectionChanged += _itemsChanged;
            Invalidate();
        }
    }

    public event EventHandler<CalendarItemEventArgs>? ItemClicked;
    public event EventHandler<CalendarItemEventArgs>? ItemDoubleClicked;
    public event EventHandler<CalendarItemEventArgs>? ItemContextRequested;
    public event EventHandler<CalendarSlotEventArgs>? SlotClicked;
    public event EventHandler<CalendarSlotEventArgs>? SlotContextRequested;
    public event EventHandler<CalendarPeriodEventArgs>? PeriodSettled;
    public event EventHandler<CalendarPeriodEventArgs>? ViewportPeriodChanged;
    public event EventHandler? TodayRequested;
    public event EventHandler? Relaid;
    public event EventHandler<DateOnly>? MonthDayDoubleClicked;
    public event EventHandler? CopyRequested;
    public event EventHandler<CalendarSlotEventArgs>? PasteRequested;

    /// <summary>Redraws tiles whose model changed (selection, busy) without a relayout.</summary>
    public void RefreshTiles()
    {
        _timedLayer.RefreshModels(Tile);
        _allDayLayer.RefreshModels(Tile);
        _monthLayer.RefreshModels(Tile);
    }

    /// <summary>Lays the realized window out again.</summary>
    public void ReloadLayout() => Invalidate();

    /// <summary>A tile of the item, preferring a segment inside the viewport when the item is drawn as several bars.</summary>
    public WinoCalendarTile? FindTile(ICalendarItem item) => FindVisible(_timedLayer, item) ?? FindVisible(_allDayLayer, item) ?? FindVisible(_monthLayer, item);

    private WinoCalendarTile? FindVisible(CalendarTileLayer layer, ICalendarItem item)
    {
        WinoCalendarTile? first = null;
        var offset = RailOffset;
        var vertical = layer.Strip == CalendarStrip.Month;
        var extent = vertical ? _monthArea.GetHeight() : _root.GetWidth() - layer.OriginX;
        foreach (var tile in layer.Active)
        {
            if (!ReferenceEquals(tile.Item, item)) continue;
            first ??= tile;
            var frame = tile.Frame;
            var (start, end) = vertical ? (frame.Y - offset, frame.Bottom - offset) : (frame.X - layer.OriginX - offset, frame.Right - layer.OriginX - offset);
            if (end > 0 && start < extent) return tile;
        }
        return first;
    }

    public void FocusSurface() => _root.GrabFocus();

    internal double HourHeight => WinoCalendarSurfaceGeometry.HourHeight(_settings?.HourHeight ?? 0);

    internal CalendarSettings? CurrentSettings => _settings;

    public Gtk.Adjustment VerticalAdjustment => _scroller.GetVadjustment();

    internal Gtk.Widget StripWidget(CalendarStrip strip) => strip switch
    {
        CalendarStrip.Header => _headerOverlay,
        CalendarStrip.AllDay => _allDayOverlay,
        CalendarStrip.Month => _monthStrip,
        _ => _timedOverlay
    };

    internal CalendarTileModel Tile(ICalendarItem item, DateOnly date)
        => TileFactory?.Invoke(item, date) ?? new CalendarTileModel(item.Title ?? string.Empty, null, null,
            WinoRgba.FromHexString(item.AssignedCalendar?.BackgroundColorHex) ?? new WinoRgba(0x35, 0x84, 0xE4), CalendarItemShowAs.Busy,
            item.IsRecurringEvent, item.IsMultiDayEvent, false, false, item.IsAllDayEvent);

    partial void DrawSelection(Cairo.Context cr, CalendarStrip strip);
    partial void OnTileRealized(WinoCalendarTile tile, CalendarStrip strip);
    partial void OnTileRecycled(WinoCalendarTile tile);
    partial void OnDisposing();
    partial void OnUnmapped();
    partial void AttachInput();
    partial void AttachSlotMenu();

    private void Own(IDisposable subscription) => _owned.Add(subscription);

    private WinoCalendarTile CreateTile(CalendarStrip strip)
    {
        var tile = new WinoCalendarTile(_layouts, () => Palette);
        tile.Clicked += (sender, point) => Raise(ItemClicked, sender, point);
        tile.DoubleClicked += (sender, point) => Raise(ItemDoubleClicked, sender, point);
        tile.ContextRequested += (sender, point) => Raise(ItemContextRequested, sender, point);
        OnTileRealized(tile, strip);
        return tile;
    }

    private void Raise(EventHandler<CalendarItemEventArgs>? handler, object? sender, (double X, double Y) point)
    {
        if (sender is WinoCalendarTile { Item: { } item } tile) handler?.Invoke(this, new CalendarItemEventArgs(item, tile, point.X, point.Y));
    }

    private void TileRecycled(WinoCalendarTile tile) => OnTileRecycled(tile);

    internal void RaiseSlotClicked(CalendarSlotEventArgs args) => SlotClicked?.Invoke(this, args);

    internal void RaiseSlotContextRequested(CalendarSlotEventArgs args) => SlotContextRequested?.Invoke(this, args);

    internal void RaiseViewportPeriodChanged(CalendarPeriodEventArgs args) => ViewportPeriodChanged?.Invoke(this, args);

    internal void RaiseTodayRequested() => TodayRequested?.Invoke(this, EventArgs.Empty);

    private void Invalidate()
    {
        if (_disposed) return;
        _railDirty = true;
        RequestRelayout();
        QueueDrawAll();
    }

    private void QueueDrawAll()
    {
        _headerArea.QueueDraw();
        _allDayArea.QueueDraw();
        _timedArea.QueueDraw();
        _headerColumns.QueueDraw();
        _allDayColumns.QueueDraw();
        QueueDrawTimedColumns();
        _monthGrid.QueueDraw();
    }

    /// <summary>A drawing area of the realized window, first in the layer's moving content so tiles stay above it.</summary>
    private static void PutRailContent(CalendarTileLayer layer, Gtk.DrawingArea area)
    {
        area.SetCanTarget(false);
        area.SetContentWidth(1);
        area.SetContentHeight(1);
        layer.Content.Put(area, 0, 0);
    }

    /// <summary>Sizes the rail drawings to the realized window after a layout.</summary>
    private void SizeRailContent()
    {
        var month = IsMonth;
        _headerColumns.SetVisible(!month);
        _allDayColumns.SetVisible(!month);
        if (month) return;
        var width = Math.Max(1, (int)Math.Ceiling(_windowDates.Count * DayWidth) + 1);
        _headerColumns.SetContentWidth(width);
        _headerColumns.SetContentHeight((int)TimedHeaderHeight);
        _allDayColumns.SetContentWidth(width);
        _allDayColumns.SetContentHeight(Math.Max(1, (int)Math.Round(_allDayHeight)));
    }

    /// <summary>
    /// A relayout never runs inside an allocation or a draw: it runs once in the update phase of the next frame (the GTK
    /// twin of NeedsLayout), or on the next main-loop turn while the surface is not mapped.
    /// </summary>
    private void RequestRelayout()
    {
        if (_disposed || _relayoutRequested) return;
        _relayoutRequested = true;
        var surface = _ref;
        if (_inRelayoutTick)
        {
            // A tick callback added while the surface's tick callbacks run is never called; ask again on the next turn.
            _relayoutRequested = false;
            MainLoop.Post(() => surface.Value?.RequestRelayout());
            return;
        }
        if (_root.GetMapped())
        {
            _relayoutTick = _root.AddTickCallback((_, _) =>
            {
                if (surface.Value is { } owner)
                {
                    owner._relayoutTick = 0;
                    owner._relayoutRequested = false;
                    owner._inRelayoutTick = true;
                    try { owner.LayoutNow(); }
                    finally { owner._inRelayoutTick = false; }
                }
                return false;
            });
        }
        else
        {
            MainLoop.Post(() =>
            {
                if (surface.Value is not { } owner || !owner._relayoutRequested || owner._relayoutTick != 0) return;
                owner._relayoutRequested = false;
                owner.LayoutNow();
            });
        }
    }

    private void LayoutNow()
    {
        if (_disposed) return;
        var month = IsMonth;
        _stack.SetVisibleChildName(month ? "month" : "timed");
        _allDayOverlay.SetVisible(!month && _allDayHeight > 0);
        if (month) LayoutMonth();
        else
        {
            _headerArea.SetContentHeight((int)TimedHeaderHeight);
            var width = _timedArea.GetWidth();
            if (width <= 0) width = _root.GetWidth();
            SetUnitSize(Math.Max(0, width - HourColumnWidth) / Math.Max(1, VisibleUnitCount));
        }
        UpdateRailWindow(force: _railDirty);
        _headerArea.QueueDraw();

        if (!month && !_scrolledToStart && _windowCount > 0)
        {
            var adjustment = _scroller.GetVadjustment();
            if (adjustment.GetPageSize() > 0)
            {
                _scrolledToStart = true;
                var startHour = _settings?.IsWorkingHoursEnabled == true ? _settings.WorkingHourStart.TotalHours : 8;
                adjustment.SetValue(WinoCalendarSurfaceGeometry.InitialScrollOffset(startHour, HourHeight, WinoCalendarSurfaceGeometry.CanvasHeight(HourHeight), adjustment.GetPageSize()));
            }
            else RequestRelayout();
        }
#if DEBUG
        _layoutCount++;
#endif
        Relaid?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Lays out the realized window; called when it shifts, the unit size changes or touching items change, never per frame.</summary>
    private void RelayoutRails()
    {
        RebuildHeaderTexts();
        if (IsMonth)
        {
            _allDayLayer.Clear();
            _timedLayer.Clear();
            SyncTimedColumns();
            SizeRailContent();
            RelayoutMonth();
            return;
        }
        _monthLayer.Clear();
        RelayoutAllDay();
        _timedArea.SetContentHeight((int)WinoCalendarSurfaceGeometry.CanvasHeight(HourHeight));
        RelayoutTimed();
        SizeRailContent();
        // Only the window-wide drawings change on a shift: a timed column is keyed by its date and draws once, when it is new.
        _headerColumns.QueueDraw();
        _allDayColumns.QueueDraw();
    }

    /// <summary>Moves the strips to the current position: the only work a frame of motion does inside the window.</summary>
    private void ApplyRailOffset()
    {
        var scale = Math.Max(1, _root.GetScaleFactor());
        var offset = RailOffset;
        if (IsMonth)
        {
            _monthLayer.SetOffset(0, offset, scale);
            return;
        }
        _headerLayer.SetOffset(offset, 0, scale);
        _timedLayer.SetOffset(offset, 0, scale);
        _allDayLayer.SetOffset(offset, 0, scale);
    }

    private void OnMapped() => StartClock();

    private void StartClock()
    {
        if (_clock != 0 || _disposed) return;
        var surface = _ref;
        _clock = GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 60_000, () =>
        {
            if (surface.Value is not { } owner) return false;
            owner.QueueDrawTimedColumns();
            owner._headerColumns.QueueDraw();
            return true;
        });
    }

    private void StopClock()
    {
        if (_clock == 0) return;
        GLib.Functions.SourceRemove(_clock);
        _clock = 0;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        var surface = _ref;
        MainLoop.Post(() => surface.Value?.HandleItemsChange(args));
    }

    public void Dispose()
    {
        if (_disposed) return;
        OnDisposing();
        _disposed = true;
        _ref.Value = null;
        StopClock();
        StopFrameDriver();
        _wheelSettle?.Dispose();
        _wheelSettle = null;
        if (_relayoutTick != 0)
        {
            _root.RemoveTickCallback(_relayoutTick);
            _relayoutTick = 0;
        }
        if (_observable is not null) _observable.CollectionChanged -= _itemsChanged;
        _observable = null;
        DisposeOverflow();
        foreach (var owned in _owned) owned.Dispose();
        _owned.Clear();
        _headerLayer.Dispose();
        _timedLayer.Dispose();
        _allDayLayer.Dispose();
        _monthLayer.Dispose();
        _layouts.Dispose();
        ItemClicked = null;
        ItemDoubleClicked = null;
        ItemContextRequested = null;
        SlotClicked = null;
        SlotContextRequested = null;
        PeriodSettled = null;
        ViewportPeriodChanged = null;
        TodayRequested = null;
        Relaid = null;
        MonthDayDoubleClicked = null;
        CopyRequested = null;
        PasteRequested = null;
    }

    private sealed class SurfaceRef
    {
        public WinoCalendarSurface? Value;
    }
}
