using System.Collections.Specialized;
using System.Globalization;
using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Core.Calendar;
using Wino.Mail.Controls.Core.Layout;
using Wino.Presentation;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Calendar;

/// <summary>
/// Raised when an empty slot is tapped, or a range was dragged across slots: the start, its rectangle in surface
/// coordinates, and for a drag the exclusive <see cref="End"/>.
/// </summary>
public sealed class CalendarSlotClickedEventArgs(DateTime start, CGRect anchor, bool isAllDay, DateTime? end = null) : EventArgs
{
    public DateTime Start { get; } = start;
    public CGRect Anchor { get; } = anchor;
    public bool IsAllDay { get; } = isAllDay;

    /// <summary>The end of a dragged range (exclusive); null for a plain tap.</summary>
    public DateTime? End { get; } = end;
}

/// <summary>A tile was tapped, double-tapped or long-pressed. <see cref="Recognizer"/> is the gesture (the AppKit NSEvent).</summary>
public sealed class CalendarItemClickedEventArgs(ICalendarItem item, WinoCalendarItemView view, UIGestureRecognizer? recognizer) : EventArgs
{
    public ICalendarItem Item { get; } = item;
    public WinoCalendarItemView View { get; } = view;
    public UIGestureRecognizer? Recognizer { get; } = recognizer;
}

/// <summary>A tile was dropped at a new start (duration kept).</summary>
public sealed class CalendarItemMoveRequestedEventArgs(ICalendarItem item, DateTime start) : EventArgs
{
    public ICalendarItem Item { get; } = item;
    public DateTime Start { get; } = start;
}

/// <summary>A timed tile's bottom edge was dragged to a new end (start kept).</summary>
public sealed class CalendarItemResizeRequestedEventArgs(ICalendarItem item, DateTime end) : EventArgs
{
    public ICalendarItem Item { get; } = item;
    public DateTime End { get; } = end;
}

/// <summary>
/// The Windows CalendarPeriodControl on UIKit (twin of the AppKit <c>WinoCalendarSurfaceView</c>, same members): a day
/// header (today circled in the accent), an all-day strip, a 64pt hour gutter with a scrolling 30-minute grid, weekend
/// shading, working hours, the current-time line and reused <see cref="WinoCalendarItemView"/> tiles; or the 6x7 month
/// grid. Layout comes from the Domain <see cref="CalendarSurfaceLayoutCalculator"/>, hit testing, snapping and drag
/// math from the shared <see cref="WinoCalendarSurfaceGeometry"/>.
/// The day columns (or month week rows) sit on an infinite rail (WinoCalendarSurfaceView.Rail and .Motion,
/// docs/calendar-rail-navigation.md): each strip keeps a fixed part (the hour gutter, the all-day label, the month weekday
/// names) and a clipped rail view that holds the realized units and moves along the rail axis with the finger.
/// Touch model: a pan along the rail axis (horizontal over day columns, vertical over month rows) moves the rail and
/// glides to a period on release; a tap on a tile raises <see cref="ItemClicked"/> (a double tap also <see cref="ItemDoubleClicked"/>);
/// a tap on empty space raises <see cref="SlotClicked"/>. A long press on a tile lifts it for a move, or a resize when it
/// starts on the bottom edge, and releasing without moving raises <see cref="ItemRightClicked"/> (the context menu). A
/// long press on empty space drags out a range.
/// </summary>
public sealed partial class WinoCalendarSurfaceView : UIView
{
    public const double HourColumnWidth = WinoCalendarSurfaceGeometry.HourColumnWidth;
    public const double TimedHeaderHeight = WinoCalendarSurfaceGeometry.TimedHeaderHeight;
    public const double MonthHeaderHeight = WinoCalendarSurfaceGeometry.MonthHeaderHeight;
    internal const double TimelinePadding = WinoCalendarSurfaceGeometry.TimelinePadding;
    private const double GridIntervalMinutes = WinoCalendarSurfaceGeometry.GridIntervalMinutes;
    private const double AutoScrollEdge = 40;

    private readonly HeaderView _header;
    private readonly RailClipView _headerClip;
    private readonly HeaderRailView _headerRail;
    private readonly AllDayView _allDay;
    private readonly RailClipView _allDayClip;
    private readonly AllDayRailView _allDayRail;
    private readonly UIScrollView _scroll;
    private readonly TimedCanvasView _timed;
    private readonly RailClipView _timedClip;
    private readonly TimedRailView _timedRail;
    private readonly RailClipView _month;
    private readonly MonthRailView _monthRail;
    private NSTimer? _clock;
    private VisibleDateRange? _range;
    private CalendarSettings? _settings;
    private IReadOnlyList<ICalendarItem> _items = Array.Empty<ICalendarItem>();
    private INotifyCollectionChanged? _observableItems;
    private bool _scrolledToStart;
    private CalendarDisplayType? _lastDisplayType;
    private CalendarSelectionRange? _selectionRange;
    private DragSession? _drag;
    private SlotPress? _slotPress;
    private double _allDayHeight;

    public WinoCalendarSurfaceView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _header = new HeaderView(this);
        _headerRail = new HeaderRailView(this);
        _headerClip = new RailClipView { UserInteractionEnabled = false };
        _headerClip.AddSubview(_headerRail);
        _header.AddSubview(_headerClip);

        _allDay = new AllDayView(this);
        _allDayRail = new AllDayRailView(this);
        _allDayClip = new RailClipView();
        _allDayClip.AddSubview(_allDayRail);
        _allDay.AddSubview(_allDayClip);

        _timed = new TimedCanvasView(this);
        _timedRail = new TimedRailView(this);
        _timedClip = new RailClipView();
        _timedClip.AddSubview(_timedRail);
        _timed.AddSubview(_timedClip);

        _monthRail = new MonthRailView(this);
        _month = new RailClipView { Hidden = true };
        _month.AddSubview(_monthRail);

        _scroll = new UIScrollView { ShowsHorizontalScrollIndicator = false, AlwaysBounceVertical = true, BackgroundColor = UIColor.Clear };
        _scroll.AddSubview(_timed);
        AddSubview(_header);
        AddSubview(_allDay);
        AddSubview(_scroll);
        AddSubview(_month);
        _railPan = CreateRailPan();
        WinoTraits.OnAppearanceChanged(this, static view => view.Invalidate());
        WinoStyle.AccentChanged += AccentChanged;
    }

    /// <summary>Converts an item into what its tile shows. Set before items arrive.</summary>
    public Func<ICalendarItem, DateOnly, CalendarTileModel>? TileFactory { get; set; }

    /// <summary>Today, injectable for tests and the shared date context.</summary>
    public Func<DateOnly> Today { get; set; } = () => DateOnly.FromDateTime(DateTime.Now);

    public event EventHandler<CalendarItemClickedEventArgs>? ItemClicked;
    public event EventHandler<CalendarItemClickedEventArgs>? ItemDoubleClicked;

    /// <summary>A long press on a tile ended without a move: the context menu request (the AppKit right click).</summary>
    public event EventHandler<CalendarItemClickedEventArgs>? ItemRightClicked;

    public event EventHandler<CalendarSlotClickedEventArgs>? SlotClicked;
    public event EventHandler<CalendarItemClickedEventArgs>? ItemDragRefused;
    public event EventHandler<CalendarItemMoveRequestedEventArgs>? ItemMoveRequested;
    public event EventHandler<CalendarItemResizeRequestedEventArgs>? ItemResizeRequested;

    /// <summary>False refuses the drag (read-only calendar, locked or busy event). Null allows every item.</summary>
    public Func<ICalendarItem, bool>? CanDragItem { get; set; }

    /// <summary>Drop and range granularity in minutes (Windows TimedSelectionIntervalMinutes).</summary>
    public int DragSnapMinutes { get; set; } = 15;

    /// <summary>
    /// The period the view model shows. A confirmation of the period the rail settled on moves nothing; another period of
    /// the same kind glides there; a new display type or week layout rebuilds the rail on that period.
    /// </summary>
    public VisibleDateRange? VisibleRange
    {
        get => _range;
        set
        {
            _range = value;
            if (value?.DisplayType != _lastDisplayType) { _scrolledToStart = false; _lastDisplayType = value?.DisplayType; }
            ApplyRange(fullRelayout: false);
        }
    }

    public CalendarSettings? Settings
    {
        get => _settings;
        set
        {
            // The first scroll waits for settings; a new hour height or work-day start scrolls to the start again, because
            // the offset was computed for the previous canvas.
            if (value is not null && (_settings is null || _settings.HourHeight != value.HourHeight
                || _settings.IsWorkingHoursEnabled != value.IsWorkingHoursEnabled || _settings.WorkingHourStart != value.WorkingHourStart))
                _scrolledToStart = false;
            _settings = value;
            ApplyRange(fullRelayout: true);
        }
    }

    public IReadOnlyList<ICalendarItem> Items
    {
        get => _items;
        set
        {
            if (_observableItems is not null) _observableItems.CollectionChanged -= ItemsChanged;
            _items = value ?? Array.Empty<ICalendarItem>();
            _observableItems = value as INotifyCollectionChanged;
            if (_observableItems is not null) _observableItems.CollectionChanged += ItemsChanged;
            Invalidate();
        }
    }

    /// <summary>A range to keep highlighted (the quick event being created); null for none.</summary>
    public CalendarSelectionRange? SelectionRange
    {
        get => _selectionRange;
        set
        {
            if (Equals(_selectionRange, value)) return;
            _selectionRange = value;
            _timedRail.SetNeedsDisplay();
            _monthRail.SetNeedsDisplay();
        }
    }

    /// <summary>
    /// False hides the Day and Week column header row (weekday and date), giving its height to the grid; the iPhone shows
    /// the dates in its own period header. Month view keeps its weekday names. True by default (iPad, Mac).
    /// </summary>
    public bool ShowsDayHeader
    {
        get => _showsDayHeader;
        set
        {
            if (_showsDayHeader == value) return;
            _showsDayHeader = value;
            SetNeedsLayout();
        }
    }

    private bool _showsDayHeader = true;

    /// <summary>True while an empty-slot drag is selecting a range.</summary>
    public bool IsSelectingRange => _slotPress?.Dragging == true;

    /// <summary>True while a tile drag shows its ghost.</summary>
    public bool IsDraggingItem => _drag is not null;

    /// <summary>The target of the drag in progress, for diagnostics.</summary>
    public (DateTime Start, DateTime End)? CurrentDragTarget => _drag?.Target is { } target ? (target.Start, target.End) : null;

    /// <summary>Redraws tiles whose model changed (selection, busy) without relayout.</summary>
    public void RefreshTiles()
    {
        _timedRail.RefreshModels();
        _allDayRail.RefreshModels();
        _monthRail.RefreshModels();
    }

    /// <summary>Lays the tiles out again, for example after an item's times changed in place.</summary>
    public void ReloadLayout() => Invalidate();

    /// <summary>Drops a range drag in progress without raising anything.</summary>
    public void CancelSlotSelection()
    {
        if (_slotPress is null) return;
        var host = _slotPress.Host;
        _slotPress = null;
        host.SetNeedsDisplay();
    }

    private void AccentChanged(object? sender, EventArgs args) => Invalidate();

    /// <summary>Lays every realized unit out again and redraws every strip.</summary>
    private void Invalidate()
    {
        _railDirty = true;
        SetNeedsLayout();
        _header.SetNeedsDisplay();
        _headerRail.SetNeedsDisplay();
        _allDay.SetNeedsDisplay();
        _allDayRail.SetNeedsDisplay();
        _timed.SetNeedsDisplay();
        _timedRail.SetNeedsDisplay();
        _monthRail.SetNeedsDisplay();
    }

    public override void MovedToWindow()
    {
        base.MovedToWindow();
        _clock?.Invalidate();
        _clock = null;
        if (Window is null)
        {
            CancelSlotSelection();
            EndTileDrag(commit: false);
            LandRail();
            return;
        }
        // The run loop retains the timer, so the block holds the surface weakly.
        var self = new WeakReference<WinoCalendarSurfaceView>(this);
        _clock = NSTimer.CreateRepeatingScheduledTimer(60, _ => { if (self.TryGetTarget(out var surface)) surface._timedRail.SetNeedsDisplay(); });
    }

    internal double HourHeight => WinoCalendarSurfaceGeometry.HourHeight(_settings?.HourHeight ?? 0);
    internal double TimelineHeight => HourHeight * 24;
    internal VisibleDateRange? Range => _range;
    internal CalendarSettings? CurrentSettings => _settings;
    internal CultureInfo Culture => _settings?.CultureInfo ?? CultureInfo.CurrentCulture;

    internal static UIColor LineColor => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.08), WinoStyle.Hex(0xFFFFFF, 0.09));
    internal static UIColor MinorLineColor => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.04), WinoStyle.Hex(0xFFFFFF, 0.045));
    internal static UIColor HeaderFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.045), WinoStyle.Hex(0xFFFFFF, 0.07));
    internal static UIColor WeekendFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.035), WinoStyle.Hex(0xFFFFFF, 0.05));
    internal static UIColor NowColor => WinoStyle.Hex(0xD63031);
    private static UIColor SelectionFill => WinoStyle.Accent.ColorWithAlpha(0.18f);

    internal CalendarTileModel Tile(ICalendarItem item, DateOnly date)
        => TileFactory?.Invoke(item, date) ?? new CalendarTileModel(item.Title ?? string.Empty, null, null,
            WinoStyle.FromHexString(item.AssignedCalendar?.BackgroundColorHex) ?? WinoStyle.Accent, CalendarItemShowAs.Busy,
            item.IsRecurringEvent, item.IsMultiDayEvent, false, false);

    internal void RaiseItem(WinoCalendarItemView view, UIGestureRecognizer? recognizer, int kind)
    {
        if (view.Item is not ICalendarItem item) return;
        var args = new CalendarItemClickedEventArgs(item, view, recognizer);
        if (kind == 2) ItemDoubleClicked?.Invoke(this, args);
        else if (kind < 0) ItemRightClicked?.Invoke(this, args);
        else ItemClicked?.Invoke(this, args);
    }

    internal void RaiseSlotClicked(DateTime start, CGRect anchorInChild, UIView child, bool allDay, DateTime? end = null)
        => SlotClicked?.Invoke(this, new CalendarSlotClickedEventArgs(start, ConvertRectFromView(anchorInChild, child), allDay, end));

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        var bounds = Bounds;
        bool month = IsMonth;
        _month.Hidden = !month;
        _allDay.Hidden = month;
        _scroll.Hidden = month;
        _headerClip.Hidden = month;

        if (month)
        {
            _header.Hidden = false;
            _header.Frame = new CGRect(0, 0, bounds.Width, MonthHeaderHeight);
            _month.Frame = new CGRect(0, MonthHeaderHeight, bounds.Width, Math.Max(0, bounds.Height - MonthHeaderHeight));
            SetUnitSize(_month.Bounds.Height / Math.Max(1, VisibleUnitCount));
        }
        else
        {
            PlaceTimedFrames();
            SetUnitSize(DayAreaWidth / Math.Max(1, VisibleUnitCount));
        }

        UpdateRailWindow(force: _railDirty);
        _header.SetNeedsDisplay();

        // Opens at the work-day start from the shared calendar settings (Windows and Mac: working hours when enabled, else 8:00).
        if (!month && !_scrolledToStart && _settings is { } settings && Dates.Count > 0 && _scroll.Bounds.Height > 0)
        {
            _scrolledToStart = true;
            double startHour = settings.IsWorkingHoursEnabled ? settings.WorkingHourStart.TotalHours : 8;
            double y = WinoCalendarSurfaceGeometry.InitialScrollOffset(startHour, HourHeight, _timed.Frame.Height, _scroll.Bounds.Height);
            _scroll.ContentOffset = new CGPoint(0, y);
        }
    }

    /// <summary>The header, all-day strip and hour grid frames for the current all-day height, and their rail clips.</summary>
    private void PlaceTimedFrames()
    {
        var bounds = Bounds;
        double headerHeight = _showsDayHeader ? TimedHeaderHeight : 0;
        _header.Hidden = !_showsDayHeader;
        _header.Frame = new CGRect(0, 0, bounds.Width, headerHeight);
        _allDay.Frame = new CGRect(0, headerHeight, bounds.Width, _allDayHeight);
        double scrollTop = headerHeight + _allDayHeight;
        _scroll.Frame = new CGRect(0, scrollTop, bounds.Width, Math.Max(0, bounds.Height - scrollTop));
        double canvasHeight = WinoCalendarSurfaceGeometry.CanvasHeight(HourHeight);
        _timed.Frame = new CGRect(0, 0, bounds.Width, canvasHeight);
        _scroll.ContentSize = new CGSize(bounds.Width, canvasHeight);
        double dayArea = Math.Max(0, bounds.Width - HourColumnWidth);
        _headerClip.Frame = new CGRect(HourColumnWidth, 0, dayArea, headerHeight);
        _allDayClip.Frame = new CGRect(HourColumnWidth, 0, dayArea, _allDayHeight);
        _timedClip.Frame = new CGRect(HourColumnWidth, 0, dayArea, canvasHeight);
    }

    /// <summary>
    /// Lays out the realized window: sizes each rail to it and places the tiles. Called when the window shifts, the unit
    /// size changes or the items touching it change; never per frame.
    /// </summary>
    private void RelayoutRails()
    {
        double span = _windowCount * _unitSize;
        if (IsMonth)
        {
            _allDayRail.Clear();
            _timedRail.Clear();
            _monthRail.Frame = new CGRect(0, _monthRail.Frame.Y, _month.Bounds.Width, span);
            _monthRail.Relayout();
            return;
        }

        _monthRail.Clear();
        double allDayHeight = CalendarLayoutCalculator.GetAllDayHeight(CalendarLayoutCalculator.GetAllDayLaneCount(Dates, _items));
        if (allDayHeight != _allDayHeight)
        {
            _allDayHeight = allDayHeight;
            PlaceTimedFrames();
        }
        double width = HourColumnWidth + span;
        _headerRail.Frame = new CGRect(_headerRail.Frame.X, 0, width, _showsDayHeader ? TimedHeaderHeight : 0);
        _allDayRail.Frame = new CGRect(_allDayRail.Frame.X, 0, width, _allDayHeight);
        _timedRail.Frame = new CGRect(_timedRail.Frame.X, 0, width, WinoCalendarSurfaceGeometry.CanvasHeight(HourHeight));
        _allDayRail.Relayout();
        _timedRail.Relayout();
        _headerRail.SetNeedsDisplay();
        _allDay.SetNeedsDisplay();
        _timed.SetNeedsDisplay();
    }

    /// <summary>Moves the rails to the current position; the only work a frame of motion does inside the window.</summary>
    private void ApplyRailOffset()
    {
        double offset = AlignToScreen((_position - _windowStart) * _unitSize);
        if (IsMonth)
        {
            var frame = _monthRail.Frame;
            _monthRail.Frame = new CGRect(0, -offset, frame.Width, frame.Height);
            return;
        }

        double x = -HourColumnWidth - offset;
        foreach (var rail in new UIView[] { _headerRail, _allDayRail, _timedRail })
        {
            var frame = rail.Frame;
            rail.Frame = new CGRect(x, frame.Y, frame.Width, frame.Height);
        }
    }

    private double AlignToScreen(double value)
    {
        double scale = Window?.Screen.Scale ?? UIScreen.MainScreen.Scale;
        return scale <= 0 ? value : Math.Round(value * scale) / scale;
    }

    /// <summary>The width of the day columns in the viewport (surface width minus the gutter).</summary>
    internal double DayAreaWidth => Math.Max(0, Bounds.Width - (IsMonth ? 0 : HourColumnWidth));

    /// <summary>The width of one day column on the timed rails.</summary>
    internal double DayWidth => IsMonth ? 0 : _unitSize;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clock?.Invalidate();
            _clock = null;
            StopAllMotion();
            ReleaseFrameDriver();
            WinoStyle.AccentChanged -= AccentChanged;
            if (_observableItems is not null) _observableItems.CollectionChanged -= ItemsChanged;
        }
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- drag and range selection

    internal bool AllowsResize(ICalendarItem item, CalendarPlacementKind kind)
        => kind == CalendarPlacementKind.Timed && !item.IsMultiDayEvent && !item.IsAllDayEvent && CanDragItem?.Invoke(item) != false;

    internal static WinoLayoutFrame ToFrame(CGRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    internal static CGRect ToRect(WinoLayoutFrame frame) => new(frame.X, frame.Y, frame.Width, frame.Height);

    /// <summary>A long press began on a tile: lift it (or refuse it).</summary>
    internal void BeginTileDrag(TileHostView host, WinoCalendarItemView tile, CGPoint downPoint, UIGestureRecognizer recognizer)
    {
        EndTileDrag(commit: false);
        if (tile.Item is not ICalendarItem item || tile.Kind == CalendarPlacementKind.MultiDayGhost) return;
        if (CanDragItem?.Invoke(item) == false)
        {
            ItemDragRefused?.Invoke(this, new CalendarItemClickedEventArgs(item, tile, recognizer));
            return;
        }

        bool resize = tile.AllowsResize && downPoint.Y >= tile.Frame.Bottom - Math.Max(WinoCalendarSurfaceGeometry.ResizeHandleHeight, 12);
        var session = new DragSession(host, tile, item, resize, downPoint, recognizer);
        var ghost = new WinoCalendarDragGhostView(tile.Model, tile.Kind) { Frame = tile.Frame };
        session.Ghost = ghost;
        host.AddSubview(ghost);
        tile.Alpha = 0.4f;
        _drag = session;
        UpdateDrag(downPoint);
    }

    internal void MoveTileDrag(CGPoint point)
    {
        if (_drag is not { } drag) return;
        if (!ReferenceEquals(drag.Tile.Item, drag.Item)) { EndTileDrag(commit: false); return; }
        if (!drag.Moved && Math.Abs(point.X - drag.DownPoint.X) + Math.Abs(point.Y - drag.DownPoint.Y) >= WinoCalendarSurfaceGeometry.TileDragThreshold)
            drag.Moved = true;
        if (drag.Host == _timedRail) AutoScroll(point);
        UpdateDrag(point);
    }

    internal void EndTileDrag(bool commit)
    {
        if (_drag is not { } drag) return;
        _drag = null;
        drag.Tile.Alpha = 1;
        drag.Ghost?.RemoveFromSuperview();
        drag.Ghost?.Dispose();
        if (!commit) return;
        if (!drag.Moved)
        {
            RaiseItem(drag.Tile, drag.Recognizer, -1);
            return;
        }
        if (drag.Target is not { } target) return;

        if (drag.Resize)
        {
            if (target.End != drag.OriginalEnd) ItemResizeRequested?.Invoke(this, new CalendarItemResizeRequestedEventArgs(drag.Item, target.End));
        }
        else if (target.Start != drag.OriginalStart)
        {
            ItemMoveRequested?.Invoke(this, new CalendarItemMoveRequestedEventArgs(drag.Item, target.Start));
        }
    }

    private void UpdateDrag(CGPoint point)
    {
        if (_drag is not { } drag || drag.Ghost is not { } ghost) return;
        var target = drag.Host.ResolveDrag(drag, point) ?? drag.Target;
        if (target is not { } resolved) return;
        drag.Target = resolved;
        ghost.Frame = ToRect(resolved.Frame);
        var time = drag.Resize ? resolved.End : resolved.Start;
        ghost.SetTimeText(TimeText(time), !drag.Resize && drag.Tile.Kind != CalendarPlacementKind.Timed);
    }

    /// <summary>Scrolls the hour grid while a drag nears its top or bottom edge.</summary>
    private void AutoScroll(CGPoint pointInCanvas)
    {
        var visible = _scroll.ConvertPointFromView(pointInCanvas, _timedRail).Y - _scroll.ContentOffset.Y;
        nfloat delta = 0;
        if (visible < AutoScrollEdge) delta = -8;
        else if (visible > _scroll.Bounds.Height - AutoScrollEdge) delta = 8;
        if (delta == 0) return;
        var maximum = Math.Max(0, _scroll.ContentSize.Height - _scroll.Bounds.Height);
        var offset = Math.Clamp(_scroll.ContentOffset.Y + delta, 0, maximum);
        _scroll.ContentOffset = new CGPoint(0, offset);
    }

    private TimeSpan CellDuration(bool month) => month ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(Math.Max(1, DragSnapMinutes));

    /// <summary>A long press began on empty grid space: start a range at the cell under it.</summary>
    internal void BeginSlotPress(TileHostView host, bool month, CGPoint point)
    {
        CancelSlotSelection();
        var cell = month ? _monthRail.CellAt(point) : TimedCellAt(point);
        if (cell is null) return;
        _slotPress = new SlotPress(host, month, cell.Value) { Dragging = true, Range = CalendarSelectionRange.FromCells(cell.Value, cell.Value, CellDuration(month)) };
        host.SetNeedsDisplay();
    }

    internal void DragSlotPress(CGPoint point)
    {
        if (_slotPress is not { } press) return;
        if (!press.Month) AutoScroll(point);
        var current = press.Month ? _monthRail.CellAt(point, clamp: true) : TimedCellAt(point);
        if (current is { } cell) press.Range = CalendarSelectionRange.FromCells(press.AnchorCell, cell, CellDuration(press.Month));
        press.Host.SetNeedsDisplay();
    }

    internal void EndSlotPress(CGPoint point)
    {
        if (_slotPress is not { } press) return;
        DragSlotPress(point);
        var range = press.Range;
        CancelSlotSelection();
        if (range is null) return;
        var anchor = press.Month ? _monthRail.CellBounds(DateOnly.FromDateTime(press.AnchorCell)) : TimedSegmentBounds(range, DateOnly.FromDateTime(press.AnchorCell));
        if (anchor is not { } rect) return;
        RaiseSlotClicked(range.Start, rect, press.Host, press.Month, range.End);
    }

    private CalendarSelectionRange? DisplayedSelection => _slotPress is { Dragging: true } press ? press.Range : _selectionRange;

    /// <summary>The snap cell under <paramref name="point"/> in the hour grid, clamped to the visible days and the day's last cell.</summary>
    private DateTime? TimedCellAt(CGPoint point)
    {
        point = new CGPoint(_timedRail.ClampToVisible(point).X, point.Y);
        return WinoCalendarSurfaceGeometry.TimedCellAt(point.X, point.Y, Dates, DayWidth, HourHeight, DragSnapMinutes);
    }

    private CGRect? TimedSegmentBounds(CalendarSelectionRange range, DateOnly date)
    {
        int index = WinoCalendarSurfaceGeometry.IndexOf(Dates, date);
        if (index < 0 || range.IntersectDay(date) is not { } segment) return null;
        return ToRect(WinoCalendarSurfaceGeometry.SelectionSegmentBounds(index, segment.Start, segment.End, DayWidth, HourHeight));
    }

    internal string TimeText(DateTime value) => _settings is null ? value.ToString("t", CultureInfo.CurrentCulture) : _settings.GetTimeString(value.TimeOfDay);

    /// <summary>Hour grid: the selection in each day column, and a time chip while dragging.</summary>
    internal void DrawTimedSelection()
    {
        if (DisplayedSelection is not { } range) return;
        CGRect? first = null;
        foreach (var date in Dates)
        {
            if (TimedSegmentBounds(range, date) is not { } rect) continue;
            first ??= rect;
            DrawSelectionRect(rect);
        }
        if (_slotPress is { Dragging: true } && first is { } chipRect)
            DrawChip($"{TimeText(range.Start)} – {TimeText(range.End)}", new CGPoint(chipRect.X + 4, chipRect.Y + 3));
    }

    /// <summary>Month grid: the selected day cells.</summary>
    internal void DrawMonthSelection(IReadOnlyList<CalendarMonthCell> cells)
    {
        if (DisplayedSelection is not { } range) return;
        foreach (var cell in cells)
        {
            var start = cell.Date.ToDateTime(TimeOnly.MinValue);
            if (start < range.Start.Date || start >= range.End) continue;
            DrawSelectionRect(cell.Bounds.Inset(2, 2));
        }
    }

    private static void DrawSelectionRect(CGRect rect)
    {
        var path = UIBezierPath.FromRoundedRect(rect, 4);
        SelectionFill.SetFill();
        path.Fill();
        WinoStyle.Accent.SetStroke();
        path.LineWidth = 1;
        path.Stroke();
    }

    internal static void DrawChip(string text, CGPoint origin)
    {
        var attributed = new NSAttributedString(text, new UIStringAttributes { Font = WinoStyle.Fixed(10, UIFontWeight.Semibold), ForegroundColor = UIColor.White });
        var size = attributed.Size;
        var rect = new CGRect(origin.X, origin.Y, Math.Ceiling(size.Width) + 10, Math.Ceiling(size.Height) + 2);
        WinoStyle.Hex(0x000000, 0.78).SetFill();
        UIBezierPath.FromRoundedRect(rect, 4).Fill();
        attributed.DrawString(new CGPoint(rect.X + 5, rect.Y + 1));
    }

    // ---------------------------------------------------------------- drawing helpers

    internal static void FillRect(CGRect rect) => UIBezierPath.FromRect(rect).Fill();

    internal static CGSize Measure(string text, UIFont font) => new NSAttributedString(text, new UIStringAttributes { Font = font }).Size;

    internal static void DrawText(string text, UIFont font, UIColor color, CGPoint origin)
        => new NSAttributedString(text, new UIStringAttributes { Font = font, ForegroundColor = color }).DrawString(origin);

    internal static void DrawCentered(string text, UIFont font, UIColor color, CGRect rect)
    {
        var size = Measure(text, font);
        DrawText(text, font, color, new CGPoint(rect.X + (rect.Width - size.Width) / 2, rect.Y + (rect.Height - size.Height) / 2));
    }

    // ---------------------------------------------------------------- sessions

    /// <summary>One tile drag in progress.</summary>
    internal sealed class DragSession(TileHostView host, WinoCalendarItemView tile, ICalendarItem item, bool resize, CGPoint downPoint, UIGestureRecognizer recognizer)
    {
        public TileHostView Host { get; } = host;
        public WinoCalendarItemView Tile { get; } = tile;
        public ICalendarItem Item { get; } = item;
        public bool Resize { get; } = resize;
        public CGPoint DownPoint { get; } = downPoint;
        public UIGestureRecognizer Recognizer { get; } = recognizer;
        public CGRect OriginFrame { get; } = tile.Frame;
        public DateTime OriginalStart { get; } = item.StartDate;
        public DateTime OriginalEnd { get; } = item.EndDate;
        public bool Moved { get; set; }
        public WinoCalendarDragTarget? Target { get; set; }
        public WinoCalendarDragGhostView? Ghost { get; set; }
    }

    /// <summary>One range drag on empty grid space.</summary>
    private sealed class SlotPress(TileHostView host, bool month, DateTime anchorCell)
    {
        public TileHostView Host { get; } = host;
        public bool Month { get; } = month;
        public DateTime AnchorCell { get; } = anchorCell;
        public bool Dragging { get; set; }
        public CalendarSelectionRange? Range { get; set; }
    }

    // ---------------------------------------------------------------- tile hosts

    /// <summary>A view that hosts reused tiles keyed by item and day, with the tap, double-tap and long-press gestures.</summary>
    internal abstract class TileHostView : UIView
    {
        protected readonly WinoCalendarSurfaceView Owner;
        private readonly Dictionary<(ICalendarItem Item, DateOnly Date), WinoCalendarItemView> _active = new();
        private readonly Stack<WinoCalendarItemView> _spare = new();
        private bool _pressOnTile;

        protected TileHostView(WinoCalendarSurfaceView owner)
        {
            Owner = owner;
            Opaque = false;
            BackgroundColor = UIColor.Clear;
            ContentMode = UIViewContentMode.Redraw;
            var doubleTap = new UITapGestureRecognizer(DoubleTapped) { NumberOfTapsRequired = 2 };
            AddGestureRecognizer(new UITapGestureRecognizer(Tapped));
            AddGestureRecognizer(doubleTap);
            AddGestureRecognizer(new UILongPressGestureRecognizer(LongPressed) { MinimumPressDuration = 0.35 });
        }

        protected void SyncTiles(IEnumerable<CalendarItemPlacement> placements)
        {
            var seen = new HashSet<(ICalendarItem, DateOnly)>();
            foreach (var placement in placements)
            {
                var key = (placement.Item, placement.Date);
                if (!seen.Add(key)) continue;
                if (!_active.TryGetValue(key, out var view))
                {
                    view = _spare.Count > 0 ? _spare.Pop() : new WinoCalendarItemView();
                    view.Hidden = false;
                    _active[key] = view;
                    if (view.Superview != this) AddSubview(view);
                }
                view.Item = placement.Item;
                view.Kind = placement.Kind;
                view.Date = placement.Date;
                view.Model = Owner.Tile(placement.Item, placement.Date);
                view.Frame = placement.Bounds;
                view.AllowsResize = Owner.AllowsResize(placement.Item, placement.Kind);
                if (placement.Kind == CalendarPlacementKind.MultiDayGhost) SendSubviewToBack(view);
            }
            foreach (var key in _active.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                var view = _active[key];
                _active.Remove(key);
                if (Owner._drag is { } drag && ReferenceEquals(drag.Tile, view)) Owner.EndTileDrag(commit: false);
                view.Hidden = true;
                view.Item = null;
                if (_spare.Count < 64) _spare.Push(view);
                else { view.RemoveFromSuperview(); view.Dispose(); }
            }
        }

        /// <summary>Recycles every tile (the strip is hidden for the current display type).</summary>
        public void Clear()
        {
            if (_active.Count > 0) SyncTiles(Array.Empty<CalendarItemPlacement>());
        }

        /// <summary>Whether a tile currently shows <paramref name="item"/> (including multi-day ghosts).</summary>
        internal bool Shows(ICalendarItem item)
        {
            foreach (var key in _active.Keys) if (ReferenceEquals(key.Item, item)) return true;
            return false;
        }

        /// <summary>
        /// Keeps a point inside the part of the rail the viewport shows, so a drag past the edge lands on the last visible
        /// column or row instead of on a realized unit off screen.
        /// </summary>
        internal CGPoint ClampToVisible(CGPoint point)
        {
            if (Superview is not { } clip) return point;
            var visible = ConvertRectFromView(clip.Bounds, clip);
            if (visible.Width <= 1 || visible.Height <= 1) return point;
            return new CGPoint(Math.Clamp(point.X, visible.X, visible.GetMaxX() - 1), Math.Clamp(point.Y, visible.Y, visible.GetMaxY() - 1));
        }

        /// <summary>The tile currently showing <paramref name="item"/>, if any.</summary>
        internal WinoCalendarItemView? FindTile(ICalendarItem item)
            => _active.FirstOrDefault(pair => ReferenceEquals(pair.Key.Item, item) && pair.Value.Kind != CalendarPlacementKind.MultiDayGhost).Value;

        /// <summary>Where a dragged tile lands for a point in host coordinates; null to keep the last target.</summary>
        internal virtual WinoCalendarDragTarget? ResolveDrag(DragSession session, CGPoint point) => null;

        /// <summary>A tap on empty space; hosts raise their slot click.</summary>
        protected virtual void SlotTapped(CGPoint point) { }

        /// <summary>Whether a long press on empty space at <paramref name="point"/> selects a range.</summary>
        protected virtual bool SelectsRanges => false;

        protected virtual bool IsMonthHost => false;

        public void RefreshModels()
        {
            foreach (var (key, view) in _active) view.Model = Owner.Tile(key.Item, key.Date);
        }

        private WinoCalendarItemView? TileAt(CGPoint point)
            => HitTest(point, null) is WinoCalendarItemView { Kind: not CalendarPlacementKind.MultiDayGhost, Hidden: false } tile ? tile : null;

        private void Tapped(UITapGestureRecognizer recognizer)
        {
            var point = recognizer.LocationInView(this);
            if (TileAt(point) is { } tile) Owner.RaiseItem(tile, recognizer, 1);
            else SlotTapped(point);
        }

        private void DoubleTapped(UITapGestureRecognizer recognizer)
        {
            if (TileAt(recognizer.LocationInView(this)) is { } tile) Owner.RaiseItem(tile, recognizer, 2);
        }

        private void LongPressed(UILongPressGestureRecognizer recognizer)
        {
            var point = recognizer.LocationInView(this);
            switch (recognizer.State)
            {
                case UIGestureRecognizerState.Began:
                    if (TileAt(point) is { } tile)
                    {
                        _pressOnTile = true;
                        Owner.BeginTileDrag(this, tile, point, recognizer);
                    }
                    else if (SelectsRanges)
                    {
                        _pressOnTile = false;
                        Owner.BeginSlotPress(this, IsMonthHost, point);
                    }
                    break;
                case UIGestureRecognizerState.Changed:
                    if (_pressOnTile) Owner.MoveTileDrag(point);
                    else Owner.DragSlotPress(point);
                    break;
                case UIGestureRecognizerState.Ended:
                    if (_pressOnTile) Owner.EndTileDrag(commit: true);
                    else Owner.EndSlotPress(point);
                    break;
                case UIGestureRecognizerState.Cancelled or UIGestureRecognizerState.Failed:
                    if (_pressOnTile) Owner.EndTileDrag(commit: false);
                    else Owner.CancelSlotSelection();
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- rail containers

    /// <summary>
    /// Clips a moving rail to the part of a strip that shows day columns or week rows, so the rail never paints over the
    /// hour gutter or the month weekday header.
    /// </summary>
    private sealed class RailClipView : UIView
    {
        public RailClipView()
        {
            ClipsToBounds = true;
            Opaque = false;
            BackgroundColor = UIColor.Clear;
        }
    }

    // ---------------------------------------------------------------- header

    /// <summary>The fixed header strip: its fill, the gutter border and the month weekday names.</summary>
    private sealed class HeaderView : UIView
    {
        private readonly WinoCalendarSurfaceView _owner;

        public HeaderView(WinoCalendarSurfaceView owner)
        {
            _owner = owner;
            Opaque = false;
            BackgroundColor = UIColor.Clear;
            ContentMode = UIViewContentMode.Redraw;
            IsAccessibilityElement = false;
        }

        public override void Draw(CGRect rect)
        {
            var bounds = Bounds;
            HeaderFill.SetFill();
            FillRect(bounds);
            LineColor.SetFill();
            FillRect(new CGRect(0, bounds.Height - 1, bounds.Width, 1));

            if (_owner.IsMonth)
            {
                var culture = _owner.Culture;
                double cellWidth = bounds.Width / CalendarLayoutCalculator.MonthColumns;
                var first = _owner.CurrentSettings?.FirstDayOfWeek ?? DayOfWeek.Monday;
                var font = WinoStyle.Fixed(12, UIFontWeight.Semibold);
                for (int column = 0; column < CalendarLayoutCalculator.MonthColumns; column++)
                {
                    var day = (DayOfWeek)(((int)first + column) % 7);
                    DrawCentered(culture.DateTimeFormat.AbbreviatedDayNames[(int)day], font, WinoStyle.SecondaryText, new CGRect(column * cellWidth, 0, cellWidth, bounds.Height));
                }
                return;
            }

            LineColor.SetFill();
            FillRect(new CGRect(HourColumnWidth - 1, 0, 1, bounds.Height));
        }
    }

    /// <summary>The day names and numbers of the realized columns, drawn on the header rail.</summary>
    private sealed class HeaderRailView : UIView
    {
        private readonly WinoCalendarSurfaceView _owner;

        public HeaderRailView(WinoCalendarSurfaceView owner)
        {
            _owner = owner;
            Opaque = false;
            BackgroundColor = UIColor.Clear;
            ContentMode = UIViewContentMode.Redraw;
            UserInteractionEnabled = false;
            IsAccessibilityElement = false;
        }

        public override void Draw(CGRect rect)
        {
            var bounds = Bounds;
            var dates = _owner.Dates;
            if (dates.Count == 0 || _owner.IsMonth) return;
            var culture = _owner.Culture;
            var today = _owner.Today();
            double dayWidth = _owner.DayWidth;
            bool singleDay = _owner.VisibleUnitCount == 1;
            var nameFont = WinoStyle.Fixed(12);
            var numberFont = WinoStyle.Fixed(14, UIFontWeight.Semibold);
            string NameOf(DateOnly date) => singleDay
                ? date.ToDateTime(TimeOnly.MinValue).ToString("dddd", culture)
                : culture.DateTimeFormat.AbbreviatedDayNames[(int)date.DayOfWeek];
            // Narrow phone columns stack the weekday over the number. One decision for the whole rail, so a short name
            // ("Fri") never sits inline next to stacked neighbours.
            bool stacked = false;
            for (int index = 0; index < dates.Count && !stacked; index++)
            {
                var date = dates[index];
                stacked = dayWidth < Measure(NameOf(date), nameFont).Width + 6 + Math.Max(26, Measure(date.Day.ToString(culture), numberFont).Width) + 8;
            }
            for (int index = 0; index < dates.Count; index++)
            {
                var date = dates[index];
                double x = WinoCalendarSurfaceGeometry.DayX(index, dayWidth);
                if (x > rect.GetMaxX() || x + dayWidth < rect.X) continue;
                // Boundary lines sit just left of each column, so the one resting on the gutter is clipped.
                LineColor.SetFill();
                FillRect(new CGRect(Math.Round(x) - 1, 0, 1, bounds.Height));
                string name = NameOf(date);
                string number = date.Day.ToString(culture);
                bool isToday = date == today;
                var nameSize = Measure(name, nameFont);
                var numberSize = Measure(number, numberFont);
                double circle = 26;
                double numberWidth = isToday ? circle : numberSize.Width;
                if (stacked)
                {
                    DrawCentered(name, nameFont, WinoStyle.SecondaryText, new CGRect(x, 2, dayWidth, nameSize.Height));
                    var numberRect = new CGRect(x + (dayWidth - circle) / 2, bounds.Height - circle - 2, circle, circle);
                    if (isToday)
                    {
                        WinoStyle.Accent.SetFill();
                        UIBezierPath.FromOval(numberRect).Fill();
                    }
                    DrawCentered(number, numberFont, isToday ? UIColor.White : WinoStyle.PrimaryText, numberRect);
                    continue;
                }
                double total = nameSize.Width + 6 + numberWidth;
                double start = x + (dayWidth - total) / 2;
                DrawText(name, nameFont, WinoStyle.SecondaryText, new CGPoint(start, (bounds.Height - nameSize.Height) / 2));
                double numberX = start + nameSize.Width + 6;
                if (isToday)
                {
                    var circleRect = new CGRect(numberX, (bounds.Height - circle) / 2, circle, circle);
                    WinoStyle.Accent.SetFill();
                    UIBezierPath.FromOval(circleRect).Fill();
                    DrawCentered(number, numberFont, UIColor.White, circleRect);
                }
                else
                {
                    DrawText(number, numberFont, WinoStyle.PrimaryText, new CGPoint(numberX, (bounds.Height - numberSize.Height) / 2));
                }
            }
        }
    }

    // ---------------------------------------------------------------- all-day strip

    /// <summary>The fixed all-day strip: the "All day" label, the gutter border and the hairline above the hour grid.</summary>
    private sealed class AllDayView : UIView
    {
        public AllDayView(WinoCalendarSurfaceView owner)
        {
            _ = owner;
            Opaque = false;
            BackgroundColor = UIColor.Clear;
            ContentMode = UIViewContentMode.Redraw;
        }

        public override void Draw(CGRect rect)
        {
            var bounds = Bounds;
            if (bounds.Height <= 0) return;
            // A hairline separates the strip from the hour grid.
            nfloat hairline = 1 / (nfloat)Math.Max(1, (double)ContentScaleFactor);
            LineColor.SetFill();
            FillRect(new CGRect(0, bounds.Height - hairline, bounds.Width, hairline));
            FillRect(new CGRect(HourColumnWidth - 1, 0, 1, bounds.Height));
            var font = WinoStyle.Fixed(11);
            var size = Measure(Translator.CalendarItemAllDay, font);
            DrawText(Translator.CalendarItemAllDay, font, WinoStyle.TertiaryText, new CGPoint(HourColumnWidth - 8 - size.Width, (bounds.Height - size.Height) / 2));
        }
    }

    /// <summary>All-day lanes of the realized columns: weekend shading, column lines and the all-day tiles.</summary>
    private sealed class AllDayRailView(WinoCalendarSurfaceView owner) : TileHostView(owner)
    {
        public void Relayout()
        {
            var placements = CalendarLayoutCalculator.CalculateAllDay(Owner.Dates, Owner.Items, Owner.DayWidth)
                .Select(p => p with { Bounds = new CGRect(p.Bounds.X + HourColumnWidth, p.Bounds.Y, p.Bounds.Width, p.Bounds.Height) });
            SyncTiles(placements);
            SetNeedsDisplay();
        }

        /// <summary>All-day tiles move by whole days along the strip.</summary>
        internal override WinoCalendarDragTarget? ResolveDrag(DragSession session, CGPoint point)
            => WinoCalendarSurfaceGeometry.ResolveAllDayDrag(Owner.Dates.Count, Owner.DayWidth, session.DownPoint.X, ClampToVisible(point).X,
                session.OriginalStart, session.OriginalEnd, ToFrame(session.OriginFrame));

        public override void Draw(CGRect rect)
        {
            var bounds = Bounds;
            if (bounds.Height <= 0) return;
            // The zone's surface shows through (no band); weekend shading and day lines continue the grid below.
            var dates = Owner.Dates;
            double dayWidth = Owner.DayWidth;
            int visibleCount = Owner.VisibleUnitCount;
            nfloat hairline = 1 / (nfloat)Math.Max(1, (double)ContentScaleFactor);
            for (int index = 0; index < dates.Count; index++)
            {
                double x = WinoCalendarSurfaceGeometry.DayX(index, dayWidth);
                if (WinoCalendarSurfaceGeometry.IsShadedWeekend(dates[index], visibleCount))
                {
                    WeekendFill.SetFill();
                    FillRect(new CGRect(x, 0, dayWidth, bounds.Height - hairline));
                }
                MinorLineColor.SetFill();
                FillRect(new CGRect(Math.Round(x) - 1, 0, 1, bounds.Height));
            }
        }

        protected override void SlotTapped(CGPoint point)
        {
            var dates = Owner.Dates;
            double dayWidth = Owner.DayWidth;
            if (dayWidth <= 0 || point.X < HourColumnWidth || dates.Count == 0) return;
            int dayIndex = WinoCalendarSurfaceGeometry.DayIndexAt(point.X, dates.Count, dayWidth);
            Owner.RaiseSlotClicked(dates[dayIndex].ToDateTime(TimeOnly.MinValue), new CGRect(WinoCalendarSurfaceGeometry.DayX(dayIndex, dayWidth), 0, dayWidth, Bounds.Height), this, true);
        }
    }

    // ---------------------------------------------------------------- timed grid

    /// <summary>The scrolling hour grid's fixed part: the hour labels and the gutter border.</summary>
    private sealed class TimedCanvasView : UIView
    {
        private readonly WinoCalendarSurfaceView _owner;

        public TimedCanvasView(WinoCalendarSurfaceView owner)
        {
            _owner = owner;
            Opaque = false;
            BackgroundColor = UIColor.Clear;
            ContentMode = UIViewContentMode.Redraw;
        }

        public override void Draw(CGRect rect)
        {
            var bounds = Bounds;
            if (_owner.Dates.Count == 0) return;
            double hourHeight = _owner.HourHeight;
            var settings = _owner.CurrentSettings;
            var labelFont = WinoStyle.Fixed(11);
            for (int hour = 1; hour < 24; hour++)
            {
                double y = TimelinePadding + hour * hourHeight;
                string text = settings is null ? $"{hour:00}:00" : settings.GetTimeString(TimeSpan.FromHours(hour));
                var size = Measure(text, labelFont);
                DrawText(text, labelFont, WinoStyle.TertiaryText, new CGPoint(HourColumnWidth - 8 - size.Width, y - size.Height / 2));
            }
            LineColor.SetFill();
            FillRect(new CGRect(HourColumnWidth - 1, 0, 1, bounds.Height));
        }
    }

    /// <summary>The realized day columns of the hour grid: shading, working hours, lines, the current time, the selection and the timed tiles.</summary>
    private sealed class TimedRailView(WinoCalendarSurfaceView owner) : TileHostView(owner)
    {
        protected override bool SelectsRanges => true;

        public void Relayout()
        {
            var placements = CalendarLayoutCalculator.CalculateTimed(Owner.Dates, Owner.Items, Owner.DayWidth, Owner.HourHeight,
                    Owner.CurrentSettings?.EventDisplayMode ?? CalendarEventDisplayMode.Stacked)
                .Select(p => p with { Bounds = new CGRect(p.Bounds.X + HourColumnWidth, p.Bounds.Y + TimelinePadding, p.Bounds.Width, p.Bounds.Height) });
            SyncTiles(placements);
            SetNeedsDisplay();
        }

        public override void Draw(CGRect rect)
        {
            var bounds = Bounds;
            var dates = Owner.Dates;
            if (dates.Count == 0) return;
            double dayWidth = Owner.DayWidth;
            double hourHeight = Owner.HourHeight;
            double intervalHeight = hourHeight * GridIntervalMinutes / 60;
            int intervals = WinoCalendarSurfaceGeometry.SlotsPerDay;
            int visibleCount = Owner.VisibleUnitCount;
            var settings = Owner.CurrentSettings;
            double top = TimelinePadding;

            for (int index = 0; index < dates.Count; index++)
            {
                if (WinoCalendarSurfaceGeometry.IsShadedWeekend(dates[index], visibleCount))
                {
                    WeekendFill.SetFill();
                    FillRect(new CGRect(WinoCalendarSurfaceGeometry.DayX(index, dayWidth), 0, dayWidth, bounds.Height));
                }
            }

            // Working hours: lighten the non-working slots like the Windows DefaultHourBackground.
            if (settings?.IsWorkingHoursEnabled == true)
            {
                var offHours = WinoThemeSurfaces.Dynamic(WinoThemeSurface.CalendarDefaultHour, WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.02), WinoStyle.Hex(0x000000, 0.10)));
                var workHours = WinoThemeSurfaces.Dynamic(WinoThemeSurface.CalendarWorkHour, UIColor.Clear);
                double workStart = settings.WorkingHourStart.TotalHours, workEnd = settings.WorkingHourEnd.TotalHours;
                for (int index = 0; index < dates.Count; index++)
                {
                    bool workingDay = settings.WorkingDays.Contains(dates[index].DayOfWeek);
                    double x = WinoCalendarSurfaceGeometry.DayX(index, dayWidth);
                    offHours.SetFill();
                    if (!workingDay) { FillRect(new CGRect(x, top, dayWidth, hourHeight * 24)); continue; }
                    FillRect(new CGRect(x, top, dayWidth, workStart * hourHeight));
                    FillRect(new CGRect(x, top + workEnd * hourHeight, dayWidth, Math.Max(0, (24 - workEnd) * hourHeight)));
                    workHours.SetFill();
                    FillRect(new CGRect(x, top + workStart * hourHeight, dayWidth, Math.Max(0, (workEnd - workStart) * hourHeight)));
                }
            }

            // Horizontal 30-minute lines.
            for (int interval = 0; interval <= intervals; interval++)
            {
                double y = top + interval * intervalHeight;
                (interval % 2 == 0 ? LineColor : MinorLineColor).SetFill();
                FillRect(new CGRect(HourColumnWidth, Math.Round(y), bounds.Width - HourColumnWidth, 1));
            }

            // Day boundaries, just left of each column so the one on the gutter is clipped.
            LineColor.SetFill();
            for (int index = 0; index <= dates.Count; index++)
                FillRect(new CGRect(Math.Round(WinoCalendarSurfaceGeometry.DayX(index, dayWidth)) - 1, 0, 1, bounds.Height));

            // Current time across the period that holds today.
            if (Owner.TodaySpan() is { } span)
            {
                var now = DateTime.Now;
                double y = WinoCalendarSurfaceGeometry.TimeY(now.TimeOfDay, hourHeight);
                NowColor.SetFill();
                FillRect(new CGRect(WinoCalendarSurfaceGeometry.DayX(span.First, dayWidth), y - 1, span.Count * dayWidth, 2));
                if (span.TodayIndex >= 0)
                    UIBezierPath.FromOval(new CGRect(WinoCalendarSurfaceGeometry.DayX(span.TodayIndex, dayWidth) - 5, y - 5, 10, 10)).Fill();
            }
            Owner.DrawTimedSelection();
        }

        /// <summary>
        /// Timed tiles move by day and by the snap interval, keeping their duration; a resize keeps the start and moves
        /// the end within the start's day.
        /// </summary>
        internal override WinoCalendarDragTarget? ResolveDrag(DragSession session, CGPoint point)
        {
            point = new CGPoint(ClampToVisible(point).X, point.Y);
            return WinoCalendarSurfaceGeometry.ResolveTimedDrag(Owner.Dates, Owner.DayWidth, Owner.HourHeight, Owner.DragSnapMinutes,
                session.Resize, (session.DownPoint.X, session.DownPoint.Y), (point.X, point.Y),
                session.OriginalStart, session.OriginalEnd, ToFrame(session.OriginFrame), session.Tile.Date);
        }

        /// <summary>A tap picks the 30-minute slot.</summary>
        protected override void SlotTapped(CGPoint point)
        {
            var dates = Owner.Dates;
            double dayWidth = Owner.DayWidth;
            if (WinoCalendarSurfaceGeometry.SlotStartAt(point.X, point.Y, dates, dayWidth, Owner.HourHeight) is not { } start) return;
            int dayIndex = WinoCalendarSurfaceGeometry.DayIndexAt(point.X, dates.Count, dayWidth);
            var anchor = ToRect(WinoCalendarSurfaceGeometry.SlotBounds(dayIndex, WinoCalendarSurfaceGeometry.SlotAt(point.Y, Owner.HourHeight), dayWidth, Owner.HourHeight));
            Owner.RaiseSlotClicked(start, anchor, this, false);
        }
    }

    // ---------------------------------------------------------------- month grid

    /// <summary>The realized week rows of the month grid: day cells (dimmed outside the dominant month), lines, the selection and the month tiles.</summary>
    private sealed class MonthRailView(WinoCalendarSurfaceView owner) : TileHostView(owner)
    {
        /// <summary>Below this cell width (iPhone) a day shows only its event count; event tiles are unreadable there.</summary>
        private const double CountOnlyCellWidth = 90;

        private List<CalendarMonthCell> _cells = new();
        private readonly Dictionary<DateOnly, int> _counts = new();

        protected override bool SelectsRanges => true;

        private bool CountOnly => Bounds.Width / CalendarLayoutCalculator.MonthColumns < CountOnlyCellWidth;

        protected override bool IsMonthHost => true;

        public void Relayout()
        {
            var dates = Owner.Dates;
            if (dates.Count == 0) { _cells = new(); SyncTiles(Array.Empty<CalendarItemPlacement>()); return; }
            var (cells, items) = CalendarLayoutCalculator.CalculateMonth(dates, Owner.Items, Bounds.Width, Owner.UnitSize * CalendarLayoutCalculator.MonthRows);
            _cells = cells;
            _counts.Clear();
            if (CountOnly)
            {
                foreach (var cell in cells) _counts[cell.Date] = CountOn(cell.Date);
                SyncTiles(Array.Empty<CalendarItemPlacement>());
            }
            else SyncTiles(items);
            SetNeedsDisplay();
        }

        public override void Draw(CGRect rect)
        {
            var bounds = Bounds;
            if (_cells.Count == 0) return;
            var dominant = Owner.DominantMonth;
            var culture = Owner.Culture;
            var today = Owner.Today();
            var font = WinoStyle.Fixed(12, UIFontWeight.Medium);
            foreach (var cell in _cells)
            {
                if (!cell.Bounds.IntersectsWith(rect)) continue;
                bool inMonth = cell.Date.Month == dominant.Month && cell.Date.Year == dominant.Year;
                if (cell.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                {
                    WeekendFill.SetFill();
                    FillRect(cell.Bounds);
                }
                if (cell.Date == today)
                {
                    WinoStyle.Accent.ColorWithAlpha(0.10f).SetFill();
                    FillRect(cell.Bounds);
                }
                string label = cell.Date.Day.ToString(culture);
                if (cell.Date == today)
                {
                    var circle = new CGRect(cell.Bounds.X + 3, cell.Bounds.Y + 2, 20, 20);
                    WinoStyle.Accent.SetFill();
                    UIBezierPath.FromOval(circle).Fill();
                    DrawCentered(label, WinoStyle.Fixed(12, UIFontWeight.Semibold), UIColor.White, circle);
                }
                else
                {
                    var color = inMonth ? WinoStyle.PrimaryText : WinoStyle.PrimaryText.ColorWithAlpha(0.45f);
                    DrawText(label, font, color, new CGPoint(cell.Bounds.X + 6, cell.Bounds.Y + 3));
                }
                if (_counts.TryGetValue(cell.Date, out var count) && count > 0) DrawCount(cell.Bounds, count, inMonth);
            }
            double cellWidth = bounds.Width / CalendarLayoutCalculator.MonthColumns;
            double rowHeight = Owner.UnitSize;
            int rows = (_cells.Count + CalendarLayoutCalculator.MonthColumns - 1) / CalendarLayoutCalculator.MonthColumns;
            LineColor.SetFill();
            // Row lines sit just above each row, so the one under the weekday header is clipped.
            for (int row = 0; row <= rows; row++)
                FillRect(new CGRect(0, Math.Round(row * rowHeight) - 1, bounds.Width, 1));
            for (int column = 1; column < CalendarLayoutCalculator.MonthColumns; column++)
                FillRect(new CGRect(Math.Round(column * cellWidth), 0, 1, bounds.Height));
            Owner.DrawMonthSelection(_cells);
        }

        /// <summary>The month cells re-evaluate their dimming when the dominant month changes.</summary>
        internal void DominantMonthChanged()
        {
            SetNeedsDisplay();
        }

        /// <summary>Events that touch <paramref name="date"/> (an all-day event's exclusive end does not count).</summary>
        private int CountOn(DateOnly date)
        {
            var start = date.ToDateTime(TimeOnly.MinValue);
            var end = start.AddDays(1);
            return Owner.Items.Count(item => item.StartDate < end && (item.EndDate > start || (item.EndDate <= item.StartDate && item.StartDate >= start)));
        }

        /// <summary>The count badge: an accent capsule centred in the space under the day number.</summary>
        private static void DrawCount(CGRect cell, int count, bool inMonth)
        {
            string text = count > 99 ? "99+" : count.ToString(System.Globalization.CultureInfo.CurrentCulture);
            var font = WinoStyle.Fixed(11, UIFontWeight.Semibold);
            var size = Measure(text, font);
            double width = Math.Max(20, size.Width + 10), height = 18;
            double top = cell.Y + 24;
            var badge = new CGRect(cell.X + (cell.Width - width) / 2, top + Math.Max(0, (cell.Bottom - top - height) / 2 - 2), width, height);
            (inMonth ? WinoStyle.Accent.ColorWithAlpha(0.18f) : WinoStyle.Accent.ColorWithAlpha(0.08f)).SetFill();
            UIBezierPath.FromRoundedRect(badge, (nfloat)(height / 2)).Fill();
            DrawCentered(text, font, inMonth ? WinoStyle.Accent : WinoStyle.Accent.ColorWithAlpha(0.5f), badge);
        }

        /// <summary>Month tiles move by whole days to the cell under the finger, keeping their time of day.</summary>
        internal override WinoCalendarDragTarget? ResolveDrag(DragSession session, CGPoint point)
        {
            if (_cells.Count == 0) return null;
            var clamped = ClampToVisible(point);
            var origin = _cells.FirstOrDefault(c => c.Date == session.Tile.Date);
            var target = _cells.FirstOrDefault(c => c.Bounds.Contains(clamped));
            if (origin is null || target is null) return null;
            return WinoCalendarSurfaceGeometry.ResolveMonthDrag(origin.Date, ToFrame(origin.Bounds), target.Date, ToFrame(target.Bounds),
                session.OriginalStart, session.OriginalEnd, ToFrame(session.OriginFrame));
        }

        /// <summary>A tap picks the day.</summary>
        protected override void SlotTapped(CGPoint point)
        {
            var cell = _cells.FirstOrDefault(c => c.Bounds.Contains(point));
            if (cell is not null) Owner.RaiseSlotClicked(cell.Date.ToDateTime(TimeOnly.MinValue), cell.Bounds, this, true);
        }

        /// <summary>The day cell under <paramref name="point"/>; with <paramref name="clamp"/>, the nearest visible cell when outside the grid.</summary>
        internal DateTime? CellAt(CGPoint point, bool clamp = false)
        {
            if (_cells.Count == 0) return null;
            if (clamp) point = ClampToVisible(point);
            var cell = _cells.FirstOrDefault(c => c.Bounds.Contains(point));
            return cell?.Date.ToDateTime(TimeOnly.MinValue);
        }

        internal CGRect? CellBounds(DateOnly date) => _cells.FirstOrDefault(c => c.Date == date)?.Bounds;
    }
}

/// <summary>
/// The tile drawn at a drag target: the tile at full opacity with a 2pt white outline and a shadow, and a small dark chip
/// with the snapped start (or, for a resize, end) time at its top.
/// </summary>
public sealed class WinoCalendarDragGhostView : UIView
{
    private readonly WinoCalendarItemView _tile = new();
    private readonly UILabel _chip;

    internal WinoCalendarDragGhostView(CalendarTileModel? model, CalendarPlacementKind kind)
    {
        UserInteractionEnabled = false;
        _tile.Model = model is null ? null : model with { IsSelected = false, IsBusy = false };
        _tile.Kind = kind;
        _tile.Layer.BorderColor = UIColor.White.CGColor;
        _tile.Layer.BorderWidth = 2;
        _tile.Layer.CornerRadius = 3;
        Layer.ShadowColor = UIColor.Black.CGColor;
        Layer.ShadowOpacity = 0.3f;
        Layer.ShadowRadius = 4;
        Layer.ShadowOffset = new CGSize(0, 2);
        _chip = new UILabel
        {
            Font = WinoStyle.Fixed(10, UIFontWeight.Semibold),
            TextColor = UIColor.White,
            BackgroundColor = WinoStyle.Hex(0x000000, 0.78),
            TextAlignment = UITextAlignment.Center,
            ClipsToBounds = true,
            Hidden = true
        };
        _chip.Layer.CornerRadius = 4;
        AddSubview(_tile);
        AddSubview(_chip);
    }

    internal void SetTimeText(string text, bool hidden)
    {
        _chip.Text = text;
        _chip.Hidden = hidden || string.IsNullOrEmpty(text);
        SetNeedsLayout();
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        _tile.Frame = Bounds;
        var size = _chip.SizeThatFits(new CGSize(nfloat.MaxValue, nfloat.MaxValue));
        var chip = new CGSize(Math.Ceiling(size.Width) + 10, Math.Ceiling(size.Height) + 2);
        // The chip sits inside the tile's top-right corner so it never leaves the canvas.
        _chip.Frame = new CGRect(Math.Max(0, Bounds.Width - chip.Width - 4), 3, chip.Width, chip.Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tile.Dispose();
        base.Dispose(disposing);
    }
}
