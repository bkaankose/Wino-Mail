using System.Collections.Specialized;
using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Calendar;

/// <summary>Raised when an empty slot is clicked: the slot start and its rectangle in surface coordinates.</summary>
public sealed class CalendarSlotClickedEventArgs(DateTime start, CGRect anchor, bool isAllDay) : EventArgs
{
    public DateTime Start { get; } = start;
    public CGRect Anchor { get; } = anchor;
    public bool IsAllDay { get; } = isAllDay;
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
/// current-time line and reused <see cref="WinoCalendarItemView"/> tiles; or the 6x7 month grid.
/// Everything is laid out by hand in <see cref="Layout"/>; the grid, header and all-day strip are
/// drawn in a few custom views. Event tiles are pooled and reused across ranges, but each tile is
/// layer-backed, so the layer count grows with the number of visible events.
/// </summary>
public sealed partial class WinoCalendarSurfaceView : NSView
{
    public const double HourColumnWidth = 64;
    public const double TimedHeaderHeight = 44;
    public const double MonthHeaderHeight = 36;
    internal const double TimelinePadding = 8;
    private const double GridIntervalMinutes = 30;

    private readonly HeaderView _header;
    private readonly AllDayView _allDay;
    private readonly NSScrollView _scroll;
    private readonly TimedCanvasView _timed;
    private readonly MonthCanvasView _month;
    private readonly NSTimer _clock;
    private VisibleDateRange? _range;
    private CalendarSettings? _settings;
    private IReadOnlyList<ICalendarItem> _items = [];
    private INotifyCollectionChanged? _observableItems;
    private bool _scrolledToStart;
    private CalendarDisplayType? _lastDisplayType;

    public WinoCalendarSurfaceView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        _header = new HeaderView(this);
        _allDay = new AllDayView(this);
        _timed = new TimedCanvasView(this);
        _month = new MonthCanvasView(this) { Hidden = true };
        _scroll = new NSScrollView
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
        _clock = NSTimer.CreateRepeatingScheduledTimer(60, _ => _timed.NeedsDisplay = true);
        WinoStyle.AccentChanged += AccentChanged;
    }

    /// <summary>Converts an item into what its tile shows. Set before items arrive.</summary>
    public Func<ICalendarItem, DateOnly, CalendarTileModel>? TileFactory { get; set; }

    /// <summary>Today, injectable for tests and the shared date context.</summary>
    public Func<DateOnly> Today { get; set; } = () => DateOnly.FromDateTime(DateTime.Now);

    public event EventHandler<CalendarItemClickedEventArgs>? ItemClicked;
    public event EventHandler<CalendarItemClickedEventArgs>? ItemDoubleClicked;
    public event EventHandler<CalendarItemClickedEventArgs>? ItemRightClicked;
    public event EventHandler<CalendarSlotClickedEventArgs>? SlotClicked;

    public VisibleDateRange? VisibleRange
    {
        get => _range;
        set
        {
            _range = value;
            if (value?.DisplayType != _lastDisplayType) { _scrolledToStart = false; _lastDisplayType = value?.DisplayType; }
            Invalidate();
        }
    }

    public CalendarSettings? Settings
    {
        get => _settings;
        set { _settings = value; Invalidate(); }
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
        _timed.RefreshModels();
        _allDay.RefreshModels();
        _month.RefreshModels();
    }

    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (NSThread.IsMain) Invalidate();
        else BeginInvokeOnMainThread(Invalidate);
    }

    private void AccentChanged(object? sender, EventArgs args) => Invalidate();

    private void Invalidate()
    {
        NeedsLayout = true;
        _header.NeedsDisplay = true;
        _allDay.NeedsDisplay = true;
        _timed.NeedsDisplay = true;
        _month.NeedsDisplay = true;
    }

    public override bool IsFlipped => true;

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        Invalidate();
    }

    internal double HourHeight => _settings is { HourHeight: > 0 } ? _settings.HourHeight : 52;
    internal double TimelineHeight => HourHeight * 24;
    internal VisibleDateRange? Range => _range;
    internal CalendarSettings? CurrentSettings => _settings;
    internal IReadOnlyList<DateOnly> Dates => _range?.Dates ?? [];
    internal bool IsMonth => _range?.DisplayType == CalendarDisplayType.Month;
    internal IReadOnlyList<DateOnly> MonthDates => _range is null || _settings is null ? [] : CalendarLayoutCalculator.MonthGridDates(_range, _settings.FirstDayOfWeek);

    internal static NSColor LineColor => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.08), WinoStyle.Hex(0xFFFFFF, 0.09));
    internal static NSColor MinorLineColor => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.04), WinoStyle.Hex(0xFFFFFF, 0.045));
    internal static NSColor HeaderFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.045), WinoStyle.Hex(0xFFFFFF, 0.07));
    internal static NSColor WeekendFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.035), WinoStyle.Hex(0xFFFFFF, 0.05));
    internal static NSColor NowColor => WinoStyle.Hex(0xD63031);

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

        if (month)
        {
            _header.Frame = new CGRect(0, 0, bounds.Width, MonthHeaderHeight);
            _month.Frame = new CGRect(0, MonthHeaderHeight, bounds.Width, Math.Max(0, bounds.Height - MonthHeaderHeight));
            _month.Relayout();
            return;
        }

        var dates = Dates;
        int lanes = CalendarLayoutCalculator.GetAllDayLaneCount(dates, _items);
        double allDayHeight = CalendarLayoutCalculator.GetAllDayHeight(lanes);
        _header.Frame = new CGRect(0, 0, bounds.Width, TimedHeaderHeight);
        _allDay.Frame = new CGRect(0, TimedHeaderHeight, bounds.Width, allDayHeight);
        double scrollTop = TimedHeaderHeight + allDayHeight;
        _scroll.Frame = new CGRect(0, scrollTop, bounds.Width, Math.Max(0, bounds.Height - scrollTop));
        double contentWidth = _scroll.ContentSize.Width;
        _timed.Frame = new CGRect(0, 0, contentWidth, TimelineHeight + TimelinePadding * 2);
        _allDay.Relayout(contentWidth);
        _timed.Relayout();
        _header.NeedsDisplay = true;

        if (!_scrolledToStart && dates.Count > 0 && _scroll.ContentSize.Height > 0)
        {
            _scrolledToStart = true;
            double startHour = _settings?.IsWorkingHoursEnabled == true ? _settings.WorkingHourStart.TotalHours : 8;
            double y = Math.Max(0, Math.Min((startHour - 0.5) * HourHeight, _timed.Frame.Height - _scroll.ContentSize.Height));
            _scroll.ContentView.ScrollToPoint(new CGPoint(0, y));
            _scroll.ReflectScrolledClipView(_scroll.ContentView);
        }
    }

    /// <summary>The width of the day columns (surface width minus the gutter and any scroller).</summary>
    internal double DayAreaWidth => Math.Max(0, (IsMonth ? Bounds.Width : _scroll.ContentSize.Width) - (IsMonth ? 0 : HourColumnWidth));
    internal double DayWidth => Dates.Count == 0 ? 0 : DayAreaWidth / Dates.Count;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clock.Invalidate();
            WinoStyle.AccentChanged -= AccentChanged;
            if (_observableItems is not null) _observableItems.CollectionChanged -= ItemsChanged;
        }
        base.Dispose(disposing);
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

        /// <summary>The tile currently showing <paramref name="item"/>, if any.</summary>
        internal WinoCalendarItemView? FindTile(ICalendarItem item)
            => _active.FirstOrDefault(pair => ReferenceEquals(pair.Key.Item, item) && pair.Value.Kind != CalendarPlacementKind.MultiDayGhost).Value;

        /// <summary>Where a dragged tile lands for the pointer at <paramref name="point"/> (host coordinates); null to keep the last target.</summary>
        internal virtual DragTarget? ResolveDrag(DragSession session, CGPoint point) => null;

        /// <summary>The pointer offset a debug preview applies for a move of <paramref name="days"/> days and <paramref name="minutes"/> minutes.</summary>
        internal virtual CGSize PreviewOffset(int days, double minutes) => CGSize.Empty;

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

            var settings = owner.CurrentSettings;
            var culture = settings?.CultureInfo ?? System.Globalization.CultureInfo.CurrentCulture;
            var dates = owner.Dates;
            var today = owner.Today();

            if (owner.IsMonth)
            {
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

            if (dates.Count == 0) return;
            double dayWidth = owner.DayWidth;
            var nameFont = NSFont.SystemFontOfSize(12);
            var numberFont = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold);
            for (int index = 0; index < dates.Count; index++)
            {
                var date = dates[index];
                double x = HourColumnWidth + index * dayWidth;
                if (index > 0)
                {
                    LineColor.SetFill();
                    FillRect(new CGRect(x, 0, 1, bounds.Height));
                }
                string name = dates.Count == 1
                    ? date.ToDateTime(TimeOnly.MinValue).ToString("dddd", culture)
                    : culture.DateTimeFormat.AbbreviatedDayNames[(int)date.DayOfWeek];
                string number = date.Day.ToString(culture);
                bool isToday = date == today;
                var nameSize = Measure(name, nameFont);
                var numberSize = Measure(number, numberFont);
                double circle = 24;
                double numberWidth = isToday ? circle : numberSize.Width;
                double total = nameSize.Width + 6 + numberWidth;
                double start = x + (dayWidth - total) / 2;
                DrawText(name, nameFont, WinoStyle.SecondaryText, new CGPoint(start, (bounds.Height - nameSize.Height) / 2));
                double numberX = start + nameSize.Width + 6;
                if (isToday)
                {
                    var circleRect = new CGRect(numberX, (bounds.Height - circle) / 2, circle, circle);
                    WinoStyle.Accent.SetFill();
                    NSBezierPath.FromOvalInRect(circleRect).Fill();
                    DrawCentered(number, numberFont, NSColor.White, circleRect);
                }
                else
                {
                    DrawText(number, numberFont, WinoStyle.PrimaryText, new CGPoint(numberX, (bounds.Height - numberSize.Height) / 2));
                }
            }
            LineColor.SetFill();
            FillRect(new CGRect(HourColumnWidth - 1, 0, 1, bounds.Height));
        }
    }

    // ---------------------------------------------------------------- all-day strip

    private sealed class AllDayView(WinoCalendarSurfaceView owner) : TileHostView(owner)
    {
        private double _contentWidth;

        public void Relayout(double contentWidth)
        {
            _contentWidth = contentWidth;
            double dayWidth = Owner.Dates.Count == 0 ? 0 : Math.Max(0, contentWidth - HourColumnWidth) / Owner.Dates.Count;
            var placements = CalendarLayoutCalculator.CalculateAllDay(Owner.Dates, Owner.Items, dayWidth)
                .Select(p => p with { Bounds = new CGRect(p.Bounds.X + HourColumnWidth, p.Bounds.Y, p.Bounds.Width, p.Bounds.Height) });
            SyncTiles(placements);
            NeedsDisplay = true;
        }

        private double DayWidth => Owner.Dates.Count == 0 ? 0 : Math.Max(0, _contentWidth - HourColumnWidth) / Owner.Dates.Count;

        /// <summary>All-day tiles move by whole days along the strip.</summary>
        internal override DragTarget? ResolveDrag(DragSession session, CGPoint point)
        {
            var dates = Owner.Dates;
            double dayWidth = DayWidth;
            if (dayWidth <= 0 || dates.Count == 0) return null;
            int DayAt(double x) => Math.Clamp((int)Math.Floor((x - HourColumnWidth) / dayWidth), 0, dates.Count - 1);
            int delta = DayAt(point.X) - DayAt(session.DownPoint.X);
            var frame = session.OriginFrame;
            return new DragTarget(session.OriginalStart.AddDays(delta), session.OriginalEnd.AddDays(delta),
                new CGRect(frame.X + delta * dayWidth, frame.Y, frame.Width, frame.Height));
        }

        internal override CGSize PreviewOffset(int days, double minutes) => new(days * DayWidth, 0);

        public override void DrawRect(CGRect dirtyRect)
        {
            var bounds = Bounds;
            if (bounds.Height <= 0) return;
            HeaderFill.ColorWithAlphaComponent((nfloat)0.5).SetFill();
            FillRect(bounds);
            var dates = Owner.Dates;
            double dayWidth = dates.Count == 0 ? 0 : Math.Max(0, _contentWidth - HourColumnWidth) / dates.Count;
            var today = Owner.Today();
            for (int index = 0; index < dates.Count; index++)
            {
                double x = HourColumnWidth + index * dayWidth;
                if (dates[index].DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday && dates.Count > 1)
                {
                    WeekendFill.SetFill();
                    FillRect(new CGRect(x, 0, dayWidth, bounds.Height));
                }
                LineColor.SetFill();
                FillRect(new CGRect(x, 0, 1, bounds.Height));
            }
            LineColor.SetFill();
            FillRect(new CGRect(0, bounds.Height - 1, bounds.Width, 1));
            var font = NSFont.SystemFontOfSize(11);
            var size = Measure(Translator.CalendarItemAllDay, font);
            DrawText(Translator.CalendarItemAllDay, font, WinoStyle.TertiaryText, new CGPoint(HourColumnWidth - 8 - size.Width, (bounds.Height - size.Height) / 2));
        }

        public override void MouseDown(NSEvent theEvent)
        {
            var point = ConvertPointFromView(theEvent.LocationInWindow, null);
            var dates = Owner.Dates;
            double dayWidth = dates.Count == 0 ? 0 : Math.Max(0, _contentWidth - HourColumnWidth) / dates.Count;
            if (dayWidth <= 0 || point.X < HourColumnWidth) return;
            int dayIndex = Math.Clamp((int)((point.X - HourColumnWidth) / dayWidth), 0, dates.Count - 1);
            Owner.RaiseSlotClicked(dates[dayIndex].ToDateTime(TimeOnly.MinValue), new CGRect(HourColumnWidth + dayIndex * dayWidth, 0, dayWidth, Bounds.Height), this, true);
        }
    }

    // ---------------------------------------------------------------- timed grid

    private sealed class TimedCanvasView(WinoCalendarSurfaceView owner) : TileHostView(owner)
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
            var settings = Owner.CurrentSettings;
            double top = TimelinePadding;

            // Weekend columns.
            for (int index = 0; index < dates.Count; index++)
            {
                if (dates.Count > 1 && dates[index].DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                {
                    WeekendFill.SetFill();
                    FillRect(new CGRect(HourColumnWidth + index * dayWidth, 0, dayWidth, bounds.Height));
                }
            }

            // Working hours: lighten the non-working slots like the Windows DefaultHourBackground.
            if (settings?.IsWorkingHoursEnabled == true)
            {
                var offHours = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.02), WinoStyle.Hex(0x000000, 0.10));
                double workStart = settings.WorkingHourStart.TotalHours, workEnd = settings.WorkingHourEnd.TotalHours;
                for (int index = 0; index < dates.Count; index++)
                {
                    bool workingDay = settings.WorkingDays.Contains(dates[index].DayOfWeek);
                    double x = HourColumnWidth + index * dayWidth;
                    offHours.SetFill();
                    if (!workingDay) { FillRect(new CGRect(x, top, dayWidth, hourHeight * 24)); continue; }
                    FillRect(new CGRect(x, top, dayWidth, workStart * hourHeight));
                    FillRect(new CGRect(x, top + workEnd * hourHeight, dayWidth, Math.Max(0, (24 - workEnd) * hourHeight)));
                }
            }

            // Horizontal 30-minute lines and hour labels.
            var labelFont = NSFont.SystemFontOfSize(11);
            for (int interval = 0; interval <= intervals; interval++)
            {
                double y = top + interval * intervalHeight;
                bool hour = interval % 2 == 0;
                (hour ? LineColor : MinorLineColor).SetFill();
                FillRect(new CGRect(HourColumnWidth, Math.Round(y), bounds.Width - HourColumnWidth, 1));
                if (hour && interval < intervals && interval > 0)
                {
                    string text = HourLabel(settings, interval / 2);
                    var size = Measure(text, labelFont);
                    DrawText(text, labelFont, WinoStyle.TertiaryText, new CGPoint(HourColumnWidth - 8 - size.Width, y - size.Height / 2));
                }
            }

            // Day separators.
            for (int index = 0; index <= dates.Count; index++)
            {
                LineColor.SetFill();
                FillRect(new CGRect(Math.Round(HourColumnWidth + index * dayWidth) - (index == dates.Count ? 1 : 0), 0, 1, bounds.Height));
            }

            // Current time.
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            if (Owner.Range?.Contains(today) == true)
            {
                double y = top + now.TimeOfDay.TotalHours * hourHeight;
                NowColor.SetFill();
                FillRect(new CGRect(HourColumnWidth, y - 1, bounds.Width - HourColumnWidth, 2));
                int dayIndex = -1;
                for (int index = 0; index < dates.Count; index++) if (dates[index] == today) dayIndex = index;
                if (dayIndex >= 0)
                    NSBezierPath.FromOvalInRect(new CGRect(HourColumnWidth + dayIndex * dayWidth - 5, y - 5, 10, 10)).Fill();
            }
        }

        /// <summary>
        /// Timed tiles move by day and by the snap interval, keeping their duration; a resize keeps the
        /// start and moves the end within the start's day (at least one snap interval long).
        /// </summary>
        internal override DragTarget? ResolveDrag(DragSession session, CGPoint point)
        {
            var dates = Owner.Dates;
            double dayWidth = Owner.DayWidth;
            double hourHeight = Owner.HourHeight;
            if (dayWidth <= 0 || dates.Count == 0 || hourHeight <= 0) return null;
            int DayAt(double x) => Math.Clamp((int)Math.Floor((x - HourColumnWidth) / dayWidth), 0, dates.Count - 1);
            double deltaMinutes = (point.Y - session.DownPoint.Y) / hourHeight * 60;
            var frame = session.OriginFrame;

            if (session.Resize)
            {
                var minimum = session.OriginalStart.AddMinutes(Owner.DragSnapMinutes);
                var maximum = session.OriginalStart.Date.AddDays(1);
                var end = Owner.Snap(session.OriginalEnd.AddMinutes(deltaMinutes));
                end = end < minimum ? minimum : end > maximum ? maximum : end;
                double height = frame.Height + (end - session.OriginalEnd).TotalHours * hourHeight;
                return new DragTarget(session.OriginalStart, end, new CGRect(frame.X, frame.Y, frame.Width, Math.Max(4, height)));
            }

            int deltaDays = DayAt(point.X) - DayAt(session.DownPoint.X);
            var duration = session.OriginalEnd - session.OriginalStart;
            var start = Owner.Snap(session.OriginalStart.AddDays(deltaDays).AddMinutes(deltaMinutes));
            var firstDay = dates[0].ToDateTime(TimeOnly.MinValue);
            var lastStart = dates[^1].ToDateTime(TimeOnly.MinValue).AddDays(1).AddMinutes(-Owner.DragSnapMinutes);
            start = start < firstDay ? firstDay : start > lastStart ? lastStart : start;

            int originIndex = IndexOf(dates, session.Tile.Date);
            int targetIndex = IndexOf(dates, DateOnly.FromDateTime(start));
            double columnOffset = frame.X - (HourColumnWidth + Math.Max(0, originIndex) * dayWidth);
            double rowOffset = frame.Y - (TimelinePadding + session.OriginalStart.TimeOfDay.TotalHours * hourHeight);
            double y = TimelinePadding + start.TimeOfDay.TotalHours * hourHeight + rowOffset;
            double bottom = TimelinePadding + 24 * hourHeight;
            double ghostHeight = Math.Max(4, Math.Min(frame.Height, bottom - y));
            return new DragTarget(start, start + duration,
                new CGRect(HourColumnWidth + Math.Max(0, targetIndex) * dayWidth + columnOffset, y, frame.Width, ghostHeight));
        }

        internal override CGSize PreviewOffset(int days, double minutes) => new(days * Owner.DayWidth, minutes / 60 * Owner.HourHeight);

        private static int IndexOf(IReadOnlyList<DateOnly> dates, DateOnly date)
        {
            for (int index = 0; index < dates.Count; index++) if (dates[index] == date) return index;
            return -1;
        }

        private static string HourLabel(CalendarSettings? settings, int hour)
            => settings is null ? $"{hour:00}:00" : settings.GetTimeString(TimeSpan.FromHours(hour));

        public override void MouseDown(NSEvent theEvent)
        {
            var point = ConvertPointFromView(theEvent.LocationInWindow, null);
            var dates = Owner.Dates;
            double dayWidth = Owner.DayWidth;
            if (dayWidth <= 0 || point.X < HourColumnWidth) return;
            int dayIndex = Math.Clamp((int)((point.X - HourColumnWidth) / dayWidth), 0, dates.Count - 1);
            double intervalHeight = Owner.HourHeight * GridIntervalMinutes / 60;
            int slot = Math.Clamp((int)((point.Y - TimelinePadding) / intervalHeight), 0, (int)(24 * 60 / GridIntervalMinutes) - 1);
            var start = dates[dayIndex].ToDateTime(TimeOnly.MinValue).AddMinutes(slot * GridIntervalMinutes);
            var anchor = new CGRect(HourColumnWidth + dayIndex * dayWidth, TimelinePadding + slot * intervalHeight, dayWidth, intervalHeight);
            Owner.RaiseSlotClicked(start, anchor, this, false);
        }
    }

    // ---------------------------------------------------------------- month grid

    private sealed class MonthCanvasView(WinoCalendarSurfaceView owner) : TileHostView(owner)
    {
        private List<CalendarMonthCell> _cells = [];

        public void Relayout()
        {
            var dates = Owner.MonthDates;
            if (dates.Count == 0) { _cells = []; SyncTiles([]); return; }
            var (cells, items) = CalendarLayoutCalculator.CalculateMonth(dates, Owner.Items, Bounds.Width, Bounds.Height);
            _cells = cells;
            SyncTiles(items);
            NeedsDisplay = true;
        }

        public override void DrawRect(CGRect dirtyRect)
        {
            var bounds = Bounds;
            if (_cells.Count == 0) return;
            var range = Owner.Range;
            var culture = Owner.CurrentSettings?.CultureInfo ?? System.Globalization.CultureInfo.CurrentCulture;
            var today = Owner.Today();
            var font = NSFont.SystemFontOfSize(12, NSFontWeight.Medium);
            foreach (var cell in _cells)
            {
                bool inMonth = range is not null && cell.Date.Month == range.AnchorDate.Month && cell.Date.Year == range.AnchorDate.Year;
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
                var color = inMonth ? WinoStyle.PrimaryText : WinoStyle.PrimaryText.ColorWithAlphaComponent((nfloat)0.45);
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
            double cellHeight = bounds.Height / CalendarLayoutCalculator.MonthRows;
            LineColor.SetFill();
            for (int row = 0; row <= CalendarLayoutCalculator.MonthRows; row++)
                FillRect(new CGRect(0, Math.Min(bounds.Height - 1, Math.Round(row * cellHeight)), bounds.Width, 1));
            for (int column = 1; column < CalendarLayoutCalculator.MonthColumns; column++)
                FillRect(new CGRect(Math.Round(column * cellWidth), 0, 1, bounds.Height));
        }

        /// <summary>Month tiles move by whole days to the cell under the pointer, keeping their time of day.</summary>
        internal override DragTarget? ResolveDrag(DragSession session, CGPoint point)
        {
            if (_cells.Count == 0) return null;
            var clamped = new CGPoint(Math.Clamp(point.X, 0, Bounds.Width - 1), Math.Clamp(point.Y, 0, Bounds.Height - 1));
            var origin = _cells.FirstOrDefault(c => c.Date == session.Tile.Date);
            var target = _cells.FirstOrDefault(c => c.Bounds.Contains(clamped));
            if (origin is null || target is null) return null;
            int delta = target.Date.DayNumber - origin.Date.DayNumber;
            var frame = session.OriginFrame;
            return new DragTarget(session.OriginalStart.AddDays(delta), session.OriginalEnd.AddDays(delta),
                new CGRect(frame.X + target.Bounds.X - origin.Bounds.X, frame.Y + target.Bounds.Y - origin.Bounds.Y, frame.Width, frame.Height));
        }

        internal override CGSize PreviewOffset(int days, double minutes)
        {
            double cellWidth = Bounds.Width / CalendarLayoutCalculator.MonthColumns;
            double cellHeight = Bounds.Height / CalendarLayoutCalculator.MonthRows;
            int rows = (int)Math.Floor(days / 7.0);
            return new CGSize((days - rows * 7) * cellWidth, rows * cellHeight);
        }

        public override void MouseDown(NSEvent theEvent)
        {
            var point = ConvertPointFromView(theEvent.LocationInWindow, null);
            var cell = _cells.FirstOrDefault(c => c.Bounds.Contains(point));
            if (cell is null) return;
            Owner.RaiseSlotClicked(cell.Date.ToDateTime(TimeOnly.MinValue), cell.Bounds, this, true);
        }
    }

    // ---------------------------------------------------------------- drawing helpers

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
