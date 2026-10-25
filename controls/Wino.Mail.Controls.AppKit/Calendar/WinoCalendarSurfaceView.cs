using System.Collections.Specialized;
using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Core.Calendar;
using Wino.Mail.Controls.Core.Layout;
using Wino.Presentation;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Calendar;

/// <summary>
/// Raised when an empty slot is clicked, or a range was dragged across slots: the start, its
/// rectangle in surface coordinates, and for a drag the exclusive <see cref="End"/>.
/// </summary>
public sealed class CalendarSlotClickedEventArgs(DateTime start, CGRect anchor, bool isAllDay, DateTime? end = null) : EventArgs
{
    public DateTime Start { get; } = start;
    public CGRect Anchor { get; } = anchor;
    public bool IsAllDay { get; } = isAllDay;

    /// <summary>The end of a dragged range (exclusive); null for a plain click.</summary>
    public DateTime? End { get; } = end;
}

public sealed class CalendarItemClickedEventArgs(ICalendarItem item, WinoCalendarItemView view, NSEvent? nativeEvent) : EventArgs
{
    public ICalendarItem Item { get; } = item;
    public WinoCalendarItemView View { get; } = view;
    public NSEvent? NativeEvent { get; } = nativeEvent;
}

/// <summary>
/// The Windows CalendarPeriodControl on AppKit: a day header (44pt, today circled in the accent),
/// an all-day strip, a 64pt hour gutter with a scrolling 30-minute grid, weekend shading, the
/// current-time line and reused <see cref="WinoCalendarItemView"/> tiles; or the month grid.
/// The day columns (or month week rows) sit on an infinite rail (WinoCalendarSurfaceView.Rail and
/// .Motion, docs/calendar-rail-navigation.md): each strip keeps a fixed part (the hour gutter, the
/// all-day label) and a clipped rail view that holds the realized units and moves along the rail
/// axis every frame. The rails draw their grid in one pass and host pooled, layer-backed tiles.
/// </summary>
public sealed partial class WinoCalendarSurfaceView : NSView
{
    public const double HourColumnWidth = WinoCalendarSurfaceGeometry.HourColumnWidth;
    public const double TimedHeaderHeight = WinoCalendarSurfaceGeometry.TimedHeaderHeight;
    public const double MonthHeaderHeight = WinoCalendarSurfaceGeometry.MonthHeaderHeight;
    internal const double TimelinePadding = WinoCalendarSurfaceGeometry.TimelinePadding;
    private const double GridIntervalMinutes = WinoCalendarSurfaceGeometry.GridIntervalMinutes;

    private readonly HeaderView _header;
    private readonly RailClipView _headerClip;
    private readonly HeaderRailView _headerRail;
    private readonly AllDayView _allDay;
    private readonly RailClipView _allDayClip;
    private readonly AllDayRailView _allDayRail;
    private readonly TimedScrollView _scroll;
    private readonly TimedCanvasView _timed;
    private readonly RailClipView _timedClip;
    private readonly TimedRailView _timedRail;
    private readonly RailClipView _month;
    private readonly MonthRailView _monthRail;
    private NSTimer? _clock;
    private VisibleDateRange? _range;
    private CalendarSettings? _settings;
    private IReadOnlyList<ICalendarItem> _items = [];
    private INotifyCollectionChanged? _observableItems;
    private bool _scrolledToStart;
    private CalendarDisplayType? _lastDisplayType;
    private double _allDayHeight;

    public WinoCalendarSurfaceView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        _header = new HeaderView(this);
        _headerRail = new HeaderRailView(this);
        _headerClip = new RailClipView(passThrough: true);
        _headerClip.AddSubview(_headerRail);
        _header.AddSubview(_headerClip);

        _allDay = new AllDayView(this);
        _allDayRail = new AllDayRailView(this);
        _allDayClip = new RailClipView(passThrough: false);
        _allDayClip.AddSubview(_allDayRail);
        _allDay.AddSubview(_allDayClip);

        _timed = new TimedCanvasView(this);
        _timedRail = new TimedRailView(this);
        _timedClip = new RailClipView(passThrough: false);
        _timedClip.AddSubview(_timedRail);
        _timed.AddSubview(_timedClip);

        _monthRail = new MonthRailView(this);
        _month = new RailClipView(passThrough: false) { Hidden = true };
        _month.AddSubview(_monthRail);

        _scroll = new TimedScrollView(this)
        {
            DocumentView = _timed,
            HasVerticalScroller = true,
            HasHorizontalScroller = false,
            AutohidesScrollers = true,
            DrawsBackground = false,
            BorderType = NSBorderType.NoBorder,
            AutomaticallyAdjustsContentInsets = false
        };
        _scroll.ContentView.PostsBoundsChangedNotifications = true;
        AddSubview(_header);
        AddSubview(_allDay);
        AddSubview(_scroll);
        AddSubview(_month);
        WinoStyle.AccentWatchers.Add(this, static surface => surface.Invalidate());
    }

    /// <summary>
    /// The current-time line ticks only while the surface is in a window. A scheduled timer is retained
    /// by the run loop and its callback retains the surface, so a timer that outlives the window would
    /// keep a detached surface alive and redrawing.
    /// </summary>
    public override void ViewDidMoveToWindow()
    {
        base.ViewDidMoveToWindow();
        if (Window is null)
        {
            _clock?.Invalidate();
            _clock = null;
        }
        else _clock ??= NSTimer.CreateRepeatingScheduledTimer(60, _ => _timedRail.NeedsDisplay = true);
    }

    /// <summary>Converts an item into what its tile shows. Set before items arrive.</summary>
    public Func<ICalendarItem, DateOnly, CalendarTileModel>? TileFactory { get; set; }

    /// <summary>Today, injectable for tests and the shared date context.</summary>
    public Func<DateOnly> Today { get; set; } = () => DateOnly.FromDateTime(DateTime.Now);

    public event EventHandler<CalendarItemClickedEventArgs>? ItemClicked;
    public event EventHandler<CalendarItemClickedEventArgs>? ItemDoubleClicked;
    public event EventHandler<CalendarItemClickedEventArgs>? ItemRightClicked;
    public event EventHandler<CalendarSlotClickedEventArgs>? SlotClicked;

    /// <summary>
    /// The period the view model shows. A confirmation of the period the rail settled on moves
    /// nothing; another period of the same kind glides there; a new display type or week layout
    /// rebuilds the rail on that period.
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
        set { _settings = value; ApplyRange(fullRelayout: true); }
    }

    public IReadOnlyList<ICalendarItem> Items
    {
        get => _items;
        set
        {
            if (_observableItems is not null) _observableItems.CollectionChanged -= ItemsChanged;
            _items = value ?? [];
            _observableItems = value as INotifyCollectionChanged;
            if (_observableItems is not null) _observableItems.CollectionChanged += ItemsChanged;
            Invalidate();
        }
    }

    /// <summary>Redraws tiles whose model changed (selection, busy) without relayout.</summary>
    public void RefreshTiles()
    {
        _timedRail.RefreshModels();
        _allDayRail.RefreshModels();
        _monthRail.RefreshModels();
    }

    /// <summary>Lays every realized unit out again and redraws every strip.</summary>
    private void Invalidate()
    {
        _railDirty = true;
        NeedsLayout = true;
        _header.NeedsDisplay = true;
        _headerRail.NeedsDisplay = true;
        _allDay.NeedsDisplay = true;
        _allDayRail.NeedsDisplay = true;
        _timed.NeedsDisplay = true;
        _timedRail.NeedsDisplay = true;
        _monthRail.NeedsDisplay = true;
    }

    public override bool IsFlipped => true;

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        Invalidate();
    }

    internal double HourHeight => WinoCalendarSurfaceGeometry.HourHeight(_settings?.HourHeight ?? 0);
    internal double TimelineHeight => HourHeight * 24;
    internal VisibleDateRange? Range => _range;
    internal CalendarSettings? CurrentSettings => _settings;

    // Dynamic colours resolve per appearance at draw time, so one instance each serves every grid line.
    internal static NSColor LineColor { get; } = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.08), WinoStyle.Hex(0xFFFFFF, 0.09));
    internal static NSColor MinorLineColor { get; } = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.04), WinoStyle.Hex(0xFFFFFF, 0.045));
    internal static NSColor HeaderFill { get; } = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.045), WinoStyle.Hex(0xFFFFFF, 0.07));
    /// <summary>The all-day lane: half the header tint (alpha applied to the tint, not replacing it).</summary>
    internal static NSColor AllDayFill { get; } = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.0225), WinoStyle.Hex(0xFFFFFF, 0.035));
    internal static NSColor WeekendFill { get; } = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.035), WinoStyle.Hex(0xFFFFFF, 0.05));
    internal static NSColor NowColor { get; } = WinoStyle.Hex(0xD63031);

    internal CalendarTileModel Tile(ICalendarItem item, DateOnly date)
        => TileFactory?.Invoke(item, date) ?? new CalendarTileModel(item.Title ?? string.Empty, null, null,
            WinoStyle.FromHexString(item.AssignedCalendar?.BackgroundColorHex) ?? WinoStyle.Accent, CalendarItemShowAs.Busy,
            item.IsRecurringEvent, item.IsMultiDayEvent, false, false);

    internal void RaiseItemClicked(WinoCalendarItemView view, NSEvent? native, int clicks)
    {
        if (view.Item is not ICalendarItem item) return;
        var args = new CalendarItemClickedEventArgs(item, view, native);
        if (clicks == 2) ItemDoubleClicked?.Invoke(this, args);
        else if (clicks < 0) ItemRightClicked?.Invoke(this, args);
        else ItemClicked?.Invoke(this, args);
    }

    internal void RaiseSlotClicked(DateTime start, CGRect anchorInChild, NSView child, bool allDay)
        => SlotClicked?.Invoke(this, new CalendarSlotClickedEventArgs(start, ConvertRectFromView(anchorInChild, child), allDay));

    public override void Layout()
    {
        base.Layout();
        var bounds = Bounds;
        bool month = IsMonth;
        _month.Hidden = !month;
        _header.Hidden = false;
        _allDay.Hidden = month;
        _scroll.Hidden = month;
        _headerClip.Hidden = month;

        if (month)
        {
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
        _header.NeedsDisplay = true;

        if (!month && !_scrolledToStart && Dates.Count > 0 && _scroll.ContentSize.Height > 0)
        {
            _scrolledToStart = true;
            double startHour = _settings?.IsWorkingHoursEnabled == true ? _settings.WorkingHourStart.TotalHours : 8;
            double y = WinoCalendarSurfaceGeometry.InitialScrollOffset(startHour, HourHeight, _timed.Frame.Height, _scroll.ContentSize.Height);
            _scroll.ContentView.ScrollToPoint(new CGPoint(0, y));
            _scroll.ReflectScrolledClipView(_scroll.ContentView);
        }
    }

    /// <summary>The header, all-day strip and hour grid frames for the current all-day height, and their rail clips.</summary>
    private void PlaceTimedFrames()
    {
        var bounds = Bounds;
        _header.Frame = new CGRect(0, 0, bounds.Width, TimedHeaderHeight);
        _allDay.Frame = new CGRect(0, TimedHeaderHeight, bounds.Width, _allDayHeight);
        double scrollTop = TimedHeaderHeight + _allDayHeight;
        _scroll.Frame = new CGRect(0, scrollTop, bounds.Width, Math.Max(0, bounds.Height - scrollTop));
        double contentWidth = _scroll.ContentSize.Width;
        double canvasHeight = TimelineHeight + TimelinePadding * 2;
        _timed.Frame = new CGRect(0, 0, contentWidth, canvasHeight);
        double dayArea = Math.Max(0, contentWidth - HourColumnWidth);
        _headerClip.Frame = new CGRect(HourColumnWidth, 0, dayArea, TimedHeaderHeight);
        _allDayClip.Frame = new CGRect(HourColumnWidth, 0, dayArea, _allDayHeight);
        _timedClip.Frame = new CGRect(HourColumnWidth, 0, dayArea, canvasHeight);
    }

    /// <summary>
    /// Lays out the realized window: sizes each rail to it and places the tiles. Called when the
    /// window shifts, the unit size changes or the items touching it change; never per frame.
    /// </summary>
    private void RelayoutRails()
    {
        double span = _windowCount * _unitSize;
        if (IsMonth)
        {
            _allDayRail.Clear();
            _timedRail.Clear();
            _monthRail.Frame = new CGRect(_monthRail.Frame.X, _monthRail.Frame.Y, _month.Bounds.Width, span);
            _monthRail.Relayout();
            return;
        }

        _monthRail.Clear();
        var (allDayLanes, hasAllDayOverflow) = CalendarLayoutCalculator.GetAllDayLaneCount(Dates, _items);
        double allDayHeight = CalendarLayoutCalculator.GetAllDayHeight(allDayLanes, hasAllDayOverflow);
        if (allDayHeight != _allDayHeight)
        {
            _allDayHeight = allDayHeight;
            PlaceTimedFrames();
        }
        double width = HourColumnWidth + span;
        _headerRail.Frame = new CGRect(_headerRail.Frame.X, 0, width, TimedHeaderHeight);
        _allDayRail.Frame = new CGRect(_allDayRail.Frame.X, 0, width, _allDayHeight);
        _timedRail.Frame = new CGRect(_timedRail.Frame.X, 0, width, TimelineHeight + TimelinePadding * 2);
        _allDayRail.Relayout();
        _timedRail.Relayout();
        _headerRail.NeedsDisplay = true;
        _allDay.NeedsDisplay = true;
        _timed.NeedsDisplay = true;
    }

    /// <summary>Moves the rails to the current position; the only work a frame of motion does inside the window.</summary>
    private void ApplyRailOffset()
    {
        double offset = AlignToBacking((_position - _windowStart) * _unitSize);
        if (IsMonth)
        {
            _monthRail.SetFrameOrigin(new CGPoint(0, -offset));
            return;
        }

        var origin = new CGPoint(-HourColumnWidth - offset, 0);
        _headerRail.SetFrameOrigin(origin);
        _allDayRail.SetFrameOrigin(origin);
        _timedRail.SetFrameOrigin(origin);
    }

    private double AlignToBacking(double value)
    {
        double scale = Window?.BackingScaleFactor ?? 2;
        return scale <= 0 ? value : Math.Round(value * scale) / scale;
    }

    /// <summary>The width of the day columns in the viewport (surface width minus the gutter and any scroller).</summary>
    internal double DayAreaWidth => Math.Max(0, (IsMonth ? Bounds.Width : _scroll.ContentSize.Width) - (IsMonth ? 0 : HourColumnWidth));

    /// <summary>The width of one day column on the timed rails.</summary>
    internal double DayWidth => IsMonth ? 0 : _unitSize;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clock?.Invalidate();
            _clock = null;
            CancelSlotSelection();
            StopAllMotion();
            ReleaseFrameDriver();
            CloseOverflow();
            WinoStyle.AccentWatchers.Remove(this);
            if (_observableItems is not null) _observableItems.CollectionChanged -= ItemsChanged;
        }
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- rail containers

    /// <summary>
    /// Clips a moving rail to the part of a strip that shows day columns or week rows, so the rail
    /// never paints over the hour gutter or the month weekday header. A pass-through clip leaves
    /// presses to the strip below it (the day header).
    /// </summary>
    private sealed class RailClipView : NSView
    {
        private readonly bool _passThrough;

        public RailClipView(bool passThrough)
        {
            _passThrough = passThrough;
            WantsLayer = true;
            ClipsToBounds = true;
        }

        public override bool IsFlipped => true;

        public override NSView? HitTest(CGPoint point) => _passThrough ? null : base.HitTest(point);
    }

    /// <summary>The hour grid scroll view; the rail sees each scroll event before it scrolls (contract point 7).</summary>
    private sealed class TimedScrollView(WinoCalendarSurfaceView owner) : NSScrollView
    {
        public override void ScrollWheel(NSEvent theEvent)
        {
            if (owner.HandleRailScroll(theEvent, passed => base.ScrollWheel(passed))) return;
            base.ScrollWheel(theEvent);
        }
    }

    // ---------------------------------------------------------------- tile pools

    /// <summary>A view that hosts reused tiles keyed by item and day.</summary>
    internal abstract class TileHostView : NSView
    {
        protected readonly WinoCalendarSurfaceView Owner;
        private readonly Dictionary<(ICalendarItem Item, DateOnly Date), WinoCalendarItemView> _active = new();
        private readonly Stack<WinoCalendarItemView> _spare = new();

        protected TileHostView(WinoCalendarSurfaceView owner)
        {
            Owner = owner;
            WantsLayer = true;
        }

        public override bool IsFlipped => true;

        protected void SyncTiles(IEnumerable<CalendarItemPlacement> placements)
        {
            var seen = new HashSet<(ICalendarItem, DateOnly)>();
            foreach (var placement in placements)
            {
                var key = (placement.Item, placement.Date);
                if (!seen.Add(key)) continue;
                if (!_active.TryGetValue(key, out var view))
                {
                    view = _spare.Count > 0 ? _spare.Pop() : CreateTile();
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
            }
            foreach (var key in _active.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                var view = _active[key];
                _active.Remove(key);
                Owner.TileRecycled(view);
                view.Hidden = true;
                view.Item = null;
                if (_spare.Count < 64) _spare.Push(view);
                else { view.RemoveFromSuperview(); view.Dispose(); }
            }
            // Ghosts behind real tiles: re-adding a subview moves it to the top.
            if (_active.Values.Any(v => v.Kind == CalendarPlacementKind.MultiDayGhost))
                foreach (var view in _active.Values.Where(v => v.Kind != CalendarPlacementKind.MultiDayGhost)) AddSubview(view);
        }

        /// <summary>Recycles every tile (the strip is hidden for the current display type).</summary>
        public void Clear()
        {
            if (_active.Count > 0) SyncTiles([]);
        }

        /// <summary>The tile currently showing <paramref name="item"/>, if any.</summary>
        internal WinoCalendarItemView? FindTile(ICalendarItem item)
            => _active.FirstOrDefault(pair => ReferenceEquals(pair.Key.Item, item) && pair.Value.Kind != CalendarPlacementKind.MultiDayGhost).Value;

        /// <summary>Whether a tile currently shows <paramref name="item"/> (including multi-day ghosts).</summary>
        internal bool Shows(ICalendarItem item)
        {
            foreach (var key in _active.Keys) if (ReferenceEquals(key.Item, item)) return true;
            return false;
        }

        /// <summary>Where a dragged tile lands for the pointer at <paramref name="point"/> (host coordinates); null to keep the last target.</summary>
        internal virtual DragTarget? ResolveDrag(DragSession session, CGPoint point) => null;

        /// <summary>The pointer offset a debug preview applies for a move of <paramref name="days"/> days and <paramref name="minutes"/> minutes.</summary>
        internal virtual CGSize PreviewOffset(int days, double minutes) => CGSize.Empty;

        /// <summary>
        /// Keeps a point inside the part of the rail the viewport shows, so a drag past the edge
        /// lands on the last visible column or row instead of on a realized unit off screen.
        /// </summary>
        internal CGPoint ClampToVisible(CGPoint point)
        {
            var visible = VisibleRect();
            if (visible.Width <= 1 || visible.Height <= 1) return point;
            return new CGPoint(Math.Clamp(point.X, visible.X, visible.GetMaxX() - 1), Math.Clamp(point.Y, visible.Y, visible.GetMaxY() - 1));
        }

        public void RefreshModels()
        {
            foreach (var (key, view) in _active)
            {
                view.Model = Owner.Tile(key.Item, key.Date);
            }
        }

        private WinoCalendarItemView CreateTile()
        {
            var tile = new WinoCalendarItemView();
            tile.Clicked += (sender, native) => Owner.RaiseItemClicked((WinoCalendarItemView)sender!, native, 1);
            tile.DoubleClicked += (sender, native) => Owner.RaiseItemClicked((WinoCalendarItemView)sender!, native, 2);
            tile.RightClicked += (sender, native) => Owner.RaiseItemClicked((WinoCalendarItemView)sender!, native, -1);
            tile.DragBegan += (sender, press) => Owner.BeginTileDrag(this, (WinoCalendarItemView)sender!, ConvertPointFromView(press.Press.LocationInWindow, null), press.Press, press.Resize);
            tile.DragMoved += (_, native) => Owner.MoveTileDrag(native);
            tile.DragEnded += (_, _) => Owner.EndTileDrag(commit: true);
            return tile;
        }

        public override void ViewDidChangeEffectiveAppearance()
        {
            base.ViewDidChangeEffectiveAppearance();
            NeedsDisplay = true;
        }
    }

    // ---------------------------------------------------------------- header

    /// <summary>The fixed header strip: its fill, the gutter border and the month weekday names. A press anywhere on it can pan.</summary>
    private sealed class HeaderView(WinoCalendarSurfaceView owner) : NSView
    {
        public override bool IsFlipped => true;

        public override void DrawRect(CGRect dirtyRect)
        {
            var bounds = Bounds;
            HeaderFill.SetFill();
            FillRect(bounds);
            LineColor.SetFill();
            FillRect(new CGRect(0, bounds.Height - 1, bounds.Width, 1));

            if (owner.IsMonth)
            {
                var settings = owner.CurrentSettings;
                var culture = settings?.CultureInfo ?? System.Globalization.CultureInfo.CurrentCulture;
                double cellWidth = bounds.Width / CalendarLayoutCalculator.MonthColumns;
                var first = settings?.FirstDayOfWeek ?? DayOfWeek.Monday;
                var font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold);
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

        public override void MouseDown(NSEvent theEvent) => owner.BeginRailPress(this, theEvent, null);
        public override void MouseDragged(NSEvent theEvent) => owner.DragSlotPress(theEvent);
        public override void MouseUp(NSEvent theEvent) => owner.EndSlotPress(theEvent);
    }

    /// <summary>
    /// The day headers of the realized columns, drawn on the header rail in the user's timed day header format
    /// (Windows CalendarPeriodControl); today sits on an accent pill.
    /// </summary>
    private sealed class HeaderRailView(WinoCalendarSurfaceView owner) : NSView
    {
        public override bool IsFlipped => true;

        public override NSView? HitTest(CGPoint point) => null;

        public override void DrawRect(CGRect dirtyRect)
        {
            var bounds = Bounds;
            var dates = owner.Dates;
            if (dates.Count == 0 || owner.IsMonth) return;
            var settings = owner.CurrentSettings;
            var culture = settings?.CultureInfo ?? System.Globalization.CultureInfo.CurrentCulture;
            var today = owner.Today();
            double dayWidth = owner.DayWidth;
            var headerFont = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold);
            for (int index = 0; index < dates.Count; index++)
            {
                var date = dates[index];
                double x = HourColumnWidth + index * dayWidth;
                if (x > dirtyRect.GetMaxX() || x + dayWidth < dirtyRect.X) continue;
                // Boundary lines sit just left of each column, so the one resting on the gutter is clipped.
                LineColor.SetFill();
                FillRect(new CGRect(Math.Round(x) - 1, 0, 1, bounds.Height));
                string text = settings?.GetTimedDayHeaderText(date) ?? date.ToDateTime(TimeOnly.MinValue).ToString("ddd dd", culture);
                bool isToday = date == today;
                var size = Measure(text, headerFont);
                var column = new CGRect(x + 4, 0, Math.Max(0, dayWidth - 8), bounds.Height);
                double pillWidth = Math.Min(column.Width, size.Width + 16);
                var pill = new CGRect(x + (dayWidth - pillWidth) / 2, (bounds.Height - 24) / 2, pillWidth, 24);
                NSGraphicsContext.GlobalSaveGraphicsState();
                NSBezierPath.ClipRect(column);
                if (isToday)
                {
                    WinoStyle.Accent.SetFill();
                    NSBezierPath.FromRoundedRect(pill, 12, 12).Fill();
                }
                DrawText(text, headerFont, isToday ? NSColor.White : WinoStyle.PrimaryText,
                    new CGPoint(x + Math.Max(4, (dayWidth - size.Width) / 2), (bounds.Height - size.Height) / 2));
                NSGraphicsContext.GlobalRestoreGraphicsState();
            }
            LineColor.SetFill();
            FillRect(new CGRect(Math.Round(HourColumnWidth + dates.Count * dayWidth) - 1, 0, 1, bounds.Height));
        }
    }

    // ---------------------------------------------------------------- all-day strip

    /// <summary>The fixed all-day strip: its fill, the "All day" label and its borders.</summary>
    private sealed class AllDayView(WinoCalendarSurfaceView owner) : NSView
    {
        public override bool IsFlipped => true;

        public override void DrawRect(CGRect dirtyRect)
        {
            var bounds = Bounds;
            if (bounds.Height <= 0) return;
            AllDayFill.SetFill();
            FillRect(bounds);
            LineColor.SetFill();
            FillRect(new CGRect(HourColumnWidth - 1, 0, 1, bounds.Height));
            FillRect(new CGRect(0, bounds.Height - 1, bounds.Width, 1));
            var font = NSFont.SystemFontOfSize(11);
            var size = Measure(Translator.CalendarItemAllDay, font);
            DrawText(Translator.CalendarItemAllDay, font, WinoStyle.TertiaryText, new CGPoint(HourColumnWidth - 8 - size.Width, (bounds.Height - size.Height) / 2));
        }

        public override void MouseDown(NSEvent theEvent) => owner.BeginRailPress(this, theEvent, null);
        public override void MouseDragged(NSEvent theEvent) => owner.DragSlotPress(theEvent);
        public override void MouseUp(NSEvent theEvent) => owner.EndSlotPress(theEvent);
    }

    /// <summary>All-day lanes of the realized columns: weekend shading, column lines and the all-day tiles.</summary>
    private sealed class AllDayRailView(WinoCalendarSurfaceView owner) : TileHostView(owner)
    {
        private List<CalendarOverflowCell> _overflows = [];

        public void Relayout()
        {
            double dayWidth = Owner.DayWidth;
            var (items, overflows) = CalendarLayoutCalculator.CalculateAllDay(Owner.Dates, Owner.Items, dayWidth);
            var placements = items.Select(p => p with { Bounds = new CGRect(p.Bounds.X + HourColumnWidth, p.Bounds.Y, p.Bounds.Width, p.Bounds.Height) });
            _overflows = overflows.ConvertAll(o => o with { Bounds = new CGRect(o.Bounds.X + HourColumnWidth, o.Bounds.Y, o.Bounds.Width, o.Bounds.Height) });
            SyncTiles(placements);
            NeedsDisplay = true;
        }

        /// <summary>All-day tiles move by whole days along the strip.</summary>
        internal override DragTarget? ResolveDrag(DragSession session, CGPoint point)
        {
            point = ClampToVisible(point);
            var target = WinoCalendarSurfaceGeometry.ResolveAllDayDrag(Owner.Dates.Count, Owner.DayWidth, session.DownPoint.X, point.X,
                session.OriginalStart, session.OriginalEnd, ToFrame(session.OriginFrame));
            return ToDragTarget(target);
        }

        internal override CGSize PreviewOffset(int days, double minutes) => new(days * Owner.DayWidth, 0);

        public override void DrawRect(CGRect dirtyRect)
        {
            var bounds = Bounds;
            if (bounds.Height <= 0) return;
            var dates = Owner.Dates;
            double dayWidth = Owner.DayWidth;
            int visibleCount = Owner.VisibleUnitCount;
            for (int index = 0; index < dates.Count; index++)
            {
                double x = HourColumnWidth + index * dayWidth;
                if (WinoCalendarSurfaceGeometry.IsShadedWeekend(dates[index], visibleCount))
                {
                    WeekendFill.SetFill();
                    FillRect(new CGRect(x, 0, dayWidth, bounds.Height - 1));
                }
                LineColor.SetFill();
                FillRect(new CGRect(Math.Round(x) - 1, 0, 1, bounds.Height));
            }
            DrawMoreLinks(_overflows);
        }

        /// <summary>A click picks the day (on release, so a drag can pan instead).</summary>
        public override void MouseDown(NSEvent theEvent)
        {
            var point = ConvertPointFromView(theEvent.LocationInWindow, null);
            if (Owner.TryShowOverflow(this, _overflows, point)) return;
            var dates = Owner.Dates;
            double dayWidth = Owner.DayWidth;
            Action? click = null;
            if (dayWidth > 0 && point.X >= HourColumnWidth && dates.Count > 0)
            {
                int dayIndex = WinoCalendarSurfaceGeometry.DayIndexAt(point.X, dates.Count, dayWidth);
                var date = dates[dayIndex];
                var anchor = new CGRect(HourColumnWidth + dayIndex * dayWidth, 0, dayWidth, Bounds.Height);
                click = () => Owner.RaiseSlotClicked(date.ToDateTime(TimeOnly.MinValue), anchor, this, true);
            }
            Owner.BeginRailPress(this, theEvent, click);
        }

        public override void MouseDragged(NSEvent theEvent) => Owner.DragSlotPress(theEvent);
        public override void MouseUp(NSEvent theEvent) => Owner.EndSlotPress(theEvent);
    }

    // ---------------------------------------------------------------- timed grid

    /// <summary>The scrolling hour grid's fixed part: the hour labels and the gutter border.</summary>
    private sealed class TimedCanvasView(WinoCalendarSurfaceView owner) : NSView
    {
        public override bool IsFlipped => true;

        public override void DrawRect(CGRect dirtyRect)
        {
            var bounds = Bounds;
            if (owner.Dates.Count == 0) return;
            double hourHeight = owner.HourHeight;
            var settings = owner.CurrentSettings;
            var labelFont = NSFont.SystemFontOfSize(11);
            for (int hour = 1; hour < 24; hour++)
            {
                double y = TimelinePadding + hour * hourHeight;
                string text = HourLabel(settings, hour);
                var size = Measure(text, labelFont);
                DrawText(text, labelFont, WinoStyle.TertiaryText, new CGPoint(HourColumnWidth - 8 - size.Width, y - size.Height / 2));
            }
            LineColor.SetFill();
            FillRect(new CGRect(HourColumnWidth - 1, 0, 1, bounds.Height));
        }

        private static string HourLabel(CalendarSettings? settings, int hour)
            => settings?.GetTimedHourLabelText(hour) ?? $"{hour:00}:00";
    }

    /// <summary>The realized day columns of the hour grid: shading, working hours, lines, the current time, the selection and the timed tiles.</summary>
    private sealed class TimedRailView(WinoCalendarSurfaceView owner) : TileHostView(owner)
    {
        public void Relayout()
        {
            double dayWidth = Owner.DayWidth;
            var placements = CalendarLayoutCalculator.CalculateTimed(Owner.Dates, Owner.Items, dayWidth, Owner.HourHeight,
                    Owner.CurrentSettings?.EventDisplayMode ?? CalendarEventDisplayMode.Stacked)
                .Select(p => p with { Bounds = new CGRect(p.Bounds.X + HourColumnWidth, p.Bounds.Y + TimelinePadding, p.Bounds.Width, p.Bounds.Height) });
            SyncTiles(placements);
            NeedsDisplay = true;
        }

        public override void DrawRect(CGRect dirtyRect)
        {
            var bounds = Bounds;
            var dates = Owner.Dates;
            if (dates.Count == 0) return;
            double dayWidth = Owner.DayWidth;
            double hourHeight = Owner.HourHeight;
            double intervalHeight = hourHeight * GridIntervalMinutes / 60;
            int intervals = (int)(24 * 60 / GridIntervalMinutes);
            int visibleCount = Owner.VisibleUnitCount;
            var settings = Owner.CurrentSettings;
            double top = TimelinePadding;

            // Weekend columns.
            for (int index = 0; index < dates.Count; index++)
            {
                if (WinoCalendarSurfaceGeometry.IsShadedWeekend(dates[index], visibleCount))
                {
                    WeekendFill.SetFill();
                    FillRect(new CGRect(HourColumnWidth + index * dayWidth, 0, dayWidth, bounds.Height));
                }
            }

            // Working hours: lighten the non-working slots like the Windows DefaultHourBackground.
            if (settings?.IsWorkingHoursEnabled == true)
            {
                var offHours = WinoThemeSurfaces.Dynamic(WinoThemeSurface.CalendarDefaultHour, WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.02), WinoStyle.Hex(0x000000, 0.10)));
                var workHours = WinoThemeSurfaces.Dynamic(WinoThemeSurface.CalendarWorkHour, NSColor.Clear);
                double workStart = settings.WorkingHourStart.TotalHours, workEnd = settings.WorkingHourEnd.TotalHours;
                for (int index = 0; index < dates.Count; index++)
                {
                    bool workingDay = settings.WorkingDays.Contains(dates[index].DayOfWeek);
                    double x = HourColumnWidth + index * dayWidth;
                    offHours.SetFill();
                    if (!workingDay) { FillRect(new CGRect(x, top, dayWidth, hourHeight * 24)); continue; }
                    FillRect(new CGRect(x, top, dayWidth, workStart * hourHeight));
                    FillRect(new CGRect(x, top + workEnd * hourHeight, dayWidth, Math.Max(0, (24 - workEnd) * hourHeight)));
                    workHours.SetFill(); FillRect(new CGRect(x, top + workStart * hourHeight, dayWidth, Math.Max(0, (workEnd - workStart) * hourHeight)));
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
                FillRect(new CGRect(Math.Round(HourColumnWidth + index * dayWidth) - 1, 0, 1, bounds.Height));

            // Current time across the period that holds today.
            if (Owner.TodaySpan() is { } span)
            {
                var now = DateTime.Now;
                double y = top + now.TimeOfDay.TotalHours * hourHeight;
                NowColor.SetFill();
                FillRect(new CGRect(HourColumnWidth + span.First * dayWidth, y - 1, span.Count * dayWidth, 2));
                if (span.TodayIndex >= 0)
                    NSBezierPath.FromOvalInRect(new CGRect(HourColumnWidth + span.TodayIndex * dayWidth - 5, y - 5, 10, 10)).Fill();
            }
            Owner.DrawTimedSelection();
        }

        /// <summary>
        /// Timed tiles move by day and by the snap interval, keeping their duration; a resize keeps the
        /// start and moves the end within the start's day (at least one snap interval long).
        /// </summary>
        internal override DragTarget? ResolveDrag(DragSession session, CGPoint point)
        {
            point = new CGPoint(ClampToVisible(point).X, point.Y);
            var target = WinoCalendarSurfaceGeometry.ResolveTimedDrag(Owner.Dates, Owner.DayWidth, Owner.HourHeight, Owner.DragSnapMinutes,
                session.Resize, (session.DownPoint.X, session.DownPoint.Y), (point.X, point.Y),
                session.OriginalStart, session.OriginalEnd, ToFrame(session.OriginFrame), session.Tile.Date);
            return ToDragTarget(target);
        }

        internal override CGSize PreviewOffset(int days, double minutes) => new(days * Owner.DayWidth, minutes / 60 * Owner.HourHeight);

        /// <summary>A click picks the 30-minute slot; a drag pans the rail or selects a range (WinoCalendarSurfaceView.Selection).</summary>
        public override void MouseDown(NSEvent theEvent)
        {
            var point = ConvertPointFromView(theEvent.LocationInWindow, null);
            var dates = Owner.Dates;
            double dayWidth = Owner.DayWidth;
            if (WinoCalendarSurfaceGeometry.SlotStartAt(point.X, point.Y, dates, dayWidth, Owner.HourHeight) is not { } start)
            {
                Owner.BeginRailPress(this, theEvent, null);
                return;
            }
            int dayIndex = WinoCalendarSurfaceGeometry.DayIndexAt(point.X, dates.Count, dayWidth);
            var anchor = ToRect(WinoCalendarSurfaceGeometry.SlotBounds(dayIndex, WinoCalendarSurfaceGeometry.SlotAt(point.Y, Owner.HourHeight), dayWidth, Owner.HourHeight));
            Owner.BeginSlotPress(this, month: false, point, theEvent, () => Owner.RaiseSlotClicked(start, anchor, this, false));
        }

        public override void MouseDragged(NSEvent theEvent) => Owner.DragSlotPress(theEvent);
        public override void MouseUp(NSEvent theEvent) => Owner.EndSlotPress(theEvent);
    }

    // ---------------------------------------------------------------- month grid

    /// <summary>The realized week rows of the month grid: day cells (dimmed outside the dominant month), lines, the selection and the month tiles.</summary>
    private sealed class MonthRailView(WinoCalendarSurfaceView owner) : TileHostView(owner)
    {
        private const double DimmedLabelOpacity = 0.45;
        private List<CalendarMonthCell> _cells = [];
        private List<CalendarOverflowCell> _overflows = [];

        public void Relayout()
        {
            var dates = Owner.Dates;
            if (dates.Count == 0) { _cells = []; _overflows = []; SyncTiles([]); return; }
            var (cells, items, overflows) = CalendarLayoutCalculator.CalculateMonth(dates, Owner.Items, Bounds.Width, Owner.UnitSize * CalendarLayoutCalculator.MonthRows);
            _cells = cells;
            _overflows = overflows;
            SyncTiles(items);
            NeedsDisplay = true;
        }

        public override void DrawRect(CGRect dirtyRect)
        {
            var bounds = Bounds;
            if (_cells.Count == 0) return;
            var dominant = Owner.DominantMonth;
            var culture = Owner.CurrentSettings?.CultureInfo ?? System.Globalization.CultureInfo.CurrentCulture;
            var today = Owner.Today();
            var font = NSFont.SystemFontOfSize(12, NSFontWeight.Medium);
            foreach (var cell in _cells)
            {
                if (!cell.Bounds.IntersectsWith(dirtyRect)) continue;
                bool inMonth = cell.Date.Month == dominant.Month && cell.Date.Year == dominant.Year;
                if (cell.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                {
                    WeekendFill.SetFill();
                    FillRect(cell.Bounds);
                }
                if (cell.Date == today)
                {
                    WinoStyle.Accent.ColorWithAlphaComponent((nfloat)0.10).SetFill();
                    FillRect(cell.Bounds);
                }
                string label = cell.Date.Day.ToString(culture);
                var color = inMonth ? WinoStyle.PrimaryText : WinoStyle.PrimaryText.ColorWithAlphaComponent((nfloat)DimmedLabelOpacity);
                if (cell.Date == today)
                {
                    var circle = new CGRect(cell.Bounds.X + 3, cell.Bounds.Y + 2, 20, 20);
                    WinoStyle.Accent.SetFill();
                    NSBezierPath.FromOvalInRect(circle).Fill();
                    DrawCentered(label, NSFont.SystemFontOfSize(12, NSFontWeight.Semibold), NSColor.White, circle);
                }
                else
                {
                    DrawText(label, font, color, new CGPoint(cell.Bounds.X + 6, cell.Bounds.Y + 3));
                }
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
            DrawMoreLinks(_overflows);
            Owner.DrawMonthSelection(_cells);
        }

        /// <summary>Month tiles move by whole days to the cell under the pointer, keeping their time of day.</summary>
        internal override DragTarget? ResolveDrag(DragSession session, CGPoint point)
        {
            if (_cells.Count == 0) return null;
            var clamped = ClampToVisible(point);
            var origin = _cells.FirstOrDefault(c => c.Date == session.Tile.Date);
            var target = _cells.FirstOrDefault(c => c.Bounds.Contains(clamped));
            if (origin is null || target is null) return null;
            return ToDragTarget(WinoCalendarSurfaceGeometry.ResolveMonthDrag(origin.Date, ToFrame(origin.Bounds), target.Date, ToFrame(target.Bounds),
                session.OriginalStart, session.OriginalEnd, ToFrame(session.OriginFrame)));
        }

        internal override CGSize PreviewOffset(int days, double minutes)
        {
            var (width, height) = WinoCalendarSurfaceGeometry.MonthPreviewOffset(days,
                Bounds.Width / CalendarLayoutCalculator.MonthColumns, Owner.UnitSize);
            return new CGSize(width, height);
        }

        /// <summary>A click picks the day; a drag pans the rail or selects a range of days (WinoCalendarSurfaceView.Selection).</summary>
        public override void MouseDown(NSEvent theEvent)
        {
            var point = ConvertPointFromView(theEvent.LocationInWindow, null);
            if (Owner.TryShowOverflow(this, _overflows, point)) return;
            var cell = _cells.FirstOrDefault(c => c.Bounds.Contains(point));
            if (cell is null) { Owner.BeginRailPress(this, theEvent, null); return; }
            Owner.BeginSlotPress(this, month: true, point, theEvent, () => Owner.RaiseSlotClicked(cell.Date.ToDateTime(TimeOnly.MinValue), cell.Bounds, this, true));
        }

        public override void MouseDragged(NSEvent theEvent) => Owner.DragSlotPress(theEvent);
        public override void MouseUp(NSEvent theEvent) => Owner.EndSlotPress(theEvent);

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

    // ---------------------------------------------------------------- drawing helpers

    internal static WinoLayoutFrame ToFrame(CGRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    internal static CGRect ToRect(WinoLayoutFrame frame) => new(frame.X, frame.Y, frame.Width, frame.Height);

    private static DragTarget? ToDragTarget(WinoCalendarDragTarget? target)
        => target is { } resolved ? new DragTarget(resolved.Start, resolved.End, ToRect(resolved.Frame)) : null;

    /// <summary>Source-over fill (NSRectFill composites with Copy, which would punch holes into the zone).</summary>
    internal static void FillRect(CGRect rect) => NSBezierPath.FromRect(rect).Fill();


    private static CGSize Measure(string text, NSFont font)
        => new NSAttributedString(text, new NSStringAttributes { Font = font }).Size;

    private static void DrawText(string text, NSFont font, NSColor color, CGPoint origin)
        => new NSAttributedString(text, new NSStringAttributes { Font = font, ForegroundColor = color }).DrawAtPoint(origin);

    private static void DrawCentered(string text, NSFont font, NSColor color, CGRect rect)
    {
        var size = Measure(text, font);
        DrawText(text, font, color, new CGPoint(rect.X + (rect.Width - size.Width) / 2, rect.Y + (rect.Height - size.Height) / 2));
    }
}
