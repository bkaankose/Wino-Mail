using System.Collections.Specialized;
using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Widget;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Core.Calendar;
using Wino.Presentation.Android;
using Wino.Presentation.Controls;

namespace Wino.Mail.Controls.Android.Calendar;

/// <summary>What one event tile shows (Android twin of the UIKit and AppKit CalendarTileModel); the page builds it.</summary>
public sealed record WinoCalendarTileModel(string Title, string? Detail, Color Fill, CalendarItemShowAs ShowAs, bool IsSelected);

/// <summary>A tile was tapped or long-pressed; <see cref="X"/> and <see cref="Y"/> are in the surface's coordinates.</summary>
public sealed class WinoCalendarItemEventArgs(ICalendarItem item, View anchor, float x, float y) : EventArgs
{
    public ICalendarItem Item { get; } = item;
    public View Anchor { get; } = anchor;
    public float X { get; } = x;
    public float Y { get; } = y;
}

/// <summary>An empty slot was tapped: the 30-minute slot's start, or the day for the all-day strip and month cells.</summary>
public sealed class WinoCalendarSlotEventArgs(DateTime start, bool isAllDay) : EventArgs
{
    public DateTime Start { get; } = start;
    public bool IsAllDay { get; } = isAllDay;
}

/// <summary>
/// The Windows calendar surface on Android (CalendarPeriodControl; the UIKit and AppKit WinoCalendarSurfaceView) for
/// the medium and expanded layouts (canvas board CalendarTablet): Day, Week and Work week show the day header, the
/// all-day lanes and the scrolling 24-hour grid with the hour gutter, weekend shading, today's column and the current
/// time line; Month shows the six-week grid with event bars, or only each day's event count where cells are phone-narrow.
/// Horizontal flings raise <see cref="SwipedNext"/> and <see cref="SwipedPrevious"/>. Placement comes from the shared
/// <see cref="CalendarSurfaceLayoutCalculator"/> and <see cref="WinoCalendarSurfaceGeometry"/>, tile colours from
/// <see cref="WinoCalendarTileAppearance"/>. Taps raise <see cref="ItemClicked"/> or <see cref="SlotClicked"/>, a long press
/// on a tile <see cref="ItemLongClicked"/>. Dragging tiles to move or resize them is not implemented on Android.
/// </summary>
[Register("com.winomail.controls.WinoCalendarSurfaceView")]
public sealed class WinoCalendarSurfaceView : LinearLayout
{
    private DayHeaderView _header = null!;
    private AllDayView _allDay = null!;
    private NestedScrollView _scroll = null!;
    private TimedView _timed = null!;
    private MonthView _month = null!;
    private VisibleDateRange? _range;
    private CalendarSettings? _settings;
    private IReadOnlyList<ICalendarItem> _items = [];
    private INotifyCollectionChanged? _observed;
    private bool _reloadPosted;
    private bool _scrolledToStart;

    public WinoCalendarSurfaceView(Context context) : base(context) => Initialize(context);
    public WinoCalendarSurfaceView(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize(context);
    private WinoCalendarSurfaceView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public event EventHandler<WinoCalendarItemEventArgs>? ItemClicked;
    public event EventHandler<WinoCalendarItemEventArgs>? ItemLongClicked;
    public event EventHandler<WinoCalendarSlotEventArgs>? SlotClicked;

    /// <summary>A horizontal fling towards the start: the next period (pages that page the surface by swiping).</summary>
    public event EventHandler? SwipedNext;

    /// <summary>A horizontal fling towards the end: the previous period.</summary>
    public event EventHandler? SwipedPrevious;

    private GestureDetector? _swipes;

    public override bool DispatchTouchEvent(MotionEvent? e)
    {
        // Only observes the gesture: taps, long presses and the vertical scroll still reach the children.
        if (e is not null && (SwipedNext is not null || SwipedPrevious is not null))
        {
            _swipes ??= new GestureDetector(Context, new SwipeListener(this));
            _swipes.OnTouchEvent(e);
        }
        return base.DispatchTouchEvent(e);
    }

    private void Flung(float deltaX)
    {
        if (Context is not { } context || Math.Abs(deltaX) < WinoTokens.PxF(context, Resource.Dimension.wino_calendar_swipe_min_distance)) return;
        bool rtl = LayoutDirection == global::Android.Views.LayoutDirection.Rtl;
        if (deltaX < 0 ^ rtl) SwipedNext?.Invoke(this, EventArgs.Empty);
        else SwipedPrevious?.Invoke(this, EventArgs.Empty);
    }

    private sealed class SwipeListener(WinoCalendarSurfaceView owner) : GestureDetector.SimpleOnGestureListener
    {
        public override bool OnDown(MotionEvent e) => true;

        public override bool OnFling(MotionEvent? e1, MotionEvent e2, float velocityX, float velocityY)
        {
            // Clearly horizontal flings only, so a vertical scroll of the hours never pages.
            if (e1 is null || Math.Abs(velocityX) < Math.Abs(velocityY) * 2) return false;
            owner.Flung(e2.GetX() - e1.GetX());
            return true;
        }
    }

    /// <summary>Builds the tile of an item on a day (title, time line, colour, selection).</summary>
    public Func<ICalendarItem, DateOnly, WinoCalendarTileModel>? TileFactory { get; set; }

    public Func<DateOnly> Today { get; set; } = () => DateOnly.FromDateTime(DateTime.Now);

    private void Initialize(Context context)
    {
        Orientation = Orientation.Vertical;
        _header = new DayHeaderView(context, this);
        _allDay = new AllDayView(context, this);
        _timed = new TimedView(context, this);
        _scroll = new NestedScrollView(context) { FillViewport = true };
        _scroll.AddView(_timed, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        _month = new MonthView(context, this) { Visibility = ViewStates.Gone };
        AddView(_header, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        AddView(_allDay, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        AddView(_scroll, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        AddView(_month, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
    }

    public VisibleDateRange? VisibleRange
    {
        get => _range;
        set
        {
            _range = value;
            bool month = value?.DisplayType == CalendarDisplayType.Month;
            _header.Visibility = _allDay.Visibility = _scroll.Visibility = month ? ViewStates.Gone : ViewStates.Visible;
            _month.Visibility = month ? ViewStates.Visible : ViewStates.Gone;
            ReloadLayout();
        }
    }

    public CalendarSettings? Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            _scrolledToStart = false;
            ReloadLayout();
        }
    }

    /// <summary>The loaded events; an observable collection is followed (coalesced into one relayout per main-loop turn).</summary>
    public IReadOnlyList<ICalendarItem> Items
    {
        get => _items;
        set
        {
            if (_observed is not null) _observed.CollectionChanged -= ItemsChanged;
            _items = value ?? [];
            _observed = _items as INotifyCollectionChanged;
            if (_observed is not null) _observed.CollectionChanged += ItemsChanged;
            ReloadLayout();
        }
    }

    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_reloadPosted) return;
        _reloadPosted = true;
        Post(() =>
        {
            _reloadPosted = false;
            ReloadLayout();
        });
    }

    /// <summary>True while the 24-hour grid can scroll up (pull to refresh waits for its top).</summary>
    public bool CanScrollUp => _scroll.Visibility == ViewStates.Visible && _scroll.CanScrollVertically(-1);

    /// <summary>Lays the tiles out again (items moved or the range changed).</summary>
    public void ReloadLayout()
    {
        _header.RequestLayout();
        _allDay.RequestLayout();
        _timed.RequestLayout();
        RefreshTiles();
    }

    /// <summary>Redraws the tiles (selection or titles changed).</summary>
    public void RefreshTiles()
    {
        _header.Invalidate();
        _allDay.Invalidate();
        _timed.Invalidate();
        _month.Invalidate();
    }

    protected override void OnDetachedFromWindow()
    {
        if (_observed is not null) _observed.CollectionChanged -= ItemsChanged;
        _observed = null;
        base.OnDetachedFromWindow();
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        if (_items is INotifyCollectionChanged observed && _observed is null)
        {
            _observed = observed;
            observed.CollectionChanged += ItemsChanged;
        }
    }

    // ---- Shared drawing ----

    private IReadOnlyList<DateOnly> Dates => _range?.Dates ?? [];
    private List<ICalendarItem> Snapshot() => [.. _items];

    private float Density => WinoUnits.Density(Context!);
    private float GutterPx => (float)WinoCalendarSurfaceGeometry.HourColumnWidth * Density;
    private double HourHeightDp => WinoCalendarSurfaceGeometry.HourHeight(_settings?.HourHeight ?? 0);

    private float DayWidthPx(int width) => Dates.Count == 0 ? 0 : Math.Max(0, width - GutterPx) / Dates.Count;

    private WinoCalendarTileModel Tile(ICalendarItem item, DateOnly date)
        => TileFactory?.Invoke(item, date) ?? new WinoCalendarTileModel(item.Title ?? string.Empty, null, WinoColors.Accent(Context!), CalendarItemShowAs.Busy, false);

    private static readonly Paint FillPaint = new(PaintFlags.AntiAlias);
    private static readonly TextPaint TitlePaint = new(PaintFlags.AntiAlias);
    private static readonly TextPaint DetailPaint = new(PaintFlags.AntiAlias);
    private static readonly TextPaint LabelPaint = new(PaintFlags.AntiAlias);

    private void DrawTile(Canvas canvas, ICalendarItem item, DateOnly date, RectF bounds, CalendarPlacementKind kind)
    {
        var context = Context!;
        var model = Tile(item, date);
        float radius = WinoTokens.PxF(context, Resource.Dimension.wino_calendar_tile_radius);
        float padding = WinoTokens.PxF(context, Resource.Dimension.wino_calendar_tile_padding);
        double opacity = WinoCalendarTileAppearance.FillOpacity(kind, false, item.IsMultiDayEvent, false);
        var fill = WinoColors.WithAlpha(model.Fill, opacity);
        FillPaint.SetStyle(Paint.Style.Fill);
        FillPaint.Color = fill;
        canvas.DrawRoundRect(bounds, radius, radius, FillPaint);
        if (kind == CalendarPlacementKind.MultiDayGhost) return;

        float stripe = (float)WinoCalendarTileAppearance.StripeWidth * Density;
        FillPaint.Color = WinoColors.Hex(WinoCalendarTileAppearance.StripeColor(model.ShowAs));
        canvas.Save();
        canvas.ClipRect(bounds);
        canvas.DrawRect(bounds.Left, bounds.Top, bounds.Left + stripe, bounds.Bottom, FillPaint);

        var text = WinoColors.ReadableText(WinoColors.Blend(model.Fill, WinoColors.ZoneFill(context), opacity));
        TitlePaint.Color = text;
        TitlePaint.TextSize = WinoTokens.PxF(context, Resource.Dimension.wino_calendar_tile_title_text);
        TitlePaint.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
        DetailPaint.Color = WinoColors.WithAlpha(text, 0.85);
        DetailPaint.TextSize = WinoTokens.PxF(context, Resource.Dimension.wino_calendar_tile_detail_text);
        float left = bounds.Left + stripe + padding;
        float available = Math.Max(0, bounds.Right - padding - left);
        var titleMetrics = TitlePaint.GetFontMetrics();
        float titleHeight = (titleMetrics?.Descent ?? 0) - (titleMetrics?.Ascent ?? 0);
        bool singleLine = kind is CalendarPlacementKind.AllDay or CalendarPlacementKind.MonthCell || bounds.Height() < titleHeight * 2 + padding;
        float baseline = singleLine
            ? bounds.CenterY() - ((titleMetrics?.Ascent ?? 0) + (titleMetrics?.Descent ?? 0)) / 2
            : bounds.Top + padding - (titleMetrics?.Ascent ?? 0);
        canvas.DrawText(Ellipsize(model.Title, TitlePaint, available), left, baseline, TitlePaint);
        if (!singleLine && !string.IsNullOrEmpty(model.Detail))
        {
            float detailBaseline = baseline + titleHeight;
            if (detailBaseline < bounds.Bottom - padding / 2) canvas.DrawText(Ellipsize(model.Detail, DetailPaint, available), left, detailBaseline, DetailPaint);
        }
        canvas.Restore();

        if (model.IsSelected)
        {
            FillPaint.SetStyle(Paint.Style.Stroke);
            FillPaint.StrokeWidth = WinoTokens.PxF(context, Resource.Dimension.wino_calendar_tile_selected_stroke);
            FillPaint.Color = WinoColors.PrimaryText(context);
            var inset = new RectF(bounds);
            inset.Inset(FillPaint.StrokeWidth / 2, FillPaint.StrokeWidth / 2);
            canvas.DrawRoundRect(inset, radius, radius, FillPaint);
            FillPaint.SetStyle(Paint.Style.Fill);
        }
    }

    private static string Ellipsize(string text, TextPaint paint, float width)
        => width <= 0 ? string.Empty : TextUtils.EllipsizeFormatted(new Java.Lang.String(text), paint, width, TextUtils.TruncateAt.End!)?.ToString() ?? string.Empty;

    private void DrawCenteredLabel(Canvas canvas, string text, float x, float centerY, Color color, int sizeResource, bool medium)
    {
        LabelPaint.Color = color;
        LabelPaint.TextAlign = Paint.Align.Center;
        LabelPaint.TextSize = WinoTokens.PxF(Context!, sizeResource);
        LabelPaint.SetTypeface(Typeface.Create(medium ? "sans-serif-medium" : "sans-serif", TypefaceStyle.Normal));
        var metrics = LabelPaint.GetFontMetrics();
        canvas.DrawText(text, x, centerY - ((metrics?.Ascent ?? 0) + (metrics?.Descent ?? 0)) / 2, LabelPaint);
    }

    private void DrawColumnShades(Canvas canvas, float top, float bottom, float dayWidth)
    {
        var context = Context!;
        var today = Today();
        for (int index = 0; index < Dates.Count; index++)
        {
            var date = Dates[index];
            float left = GutterPx + index * dayWidth;
            if (date == today) FillPaint.Color = WinoColors.CalendarTodayColumn(context);
            else if (WinoCalendarSurfaceGeometry.IsShadedWeekend(date, Dates.Count)) FillPaint.Color = WinoTokens.Color(context, Resource.Color.wino_calendar_weekend);
            else continue;
            FillPaint.SetStyle(Paint.Style.Fill);
            canvas.DrawRect(left, top, left + dayWidth, bottom, FillPaint);
        }
    }

    private void DrawGridLine(Canvas canvas, float x0, float y0, float x1, float y1, int colorResource)
    {
        FillPaint.SetStyle(Paint.Style.Fill);
        FillPaint.Color = WinoTokens.Color(Context!, colorResource);
        float stroke = WinoTokens.PxF(Context!, Resource.Dimension.wino_calendar_grid_stroke);
        if (x0 == x1) canvas.DrawRect(x0, y0, x0 + stroke, y1, FillPaint);
        else canvas.DrawRect(x0, y0, x1, y0 + stroke, FillPaint);
    }

    // ---- Day header (weekday and number per column) ----

    private sealed class DayHeaderView : View
    {
        private readonly WinoCalendarSurfaceView _owner;

        public DayHeaderView(Context context, WinoCalendarSurfaceView owner) : base(context) => _owner = owner;
        private DayHeaderView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) => _owner = null!;

        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
            => SetMeasuredDimension(MeasureSpec.GetSize(widthMeasureSpec), WinoTokens.Px(Context!, Resource.Dimension.wino_calendar_surface_day_header_height));

        protected override void OnDraw(Canvas canvas)
        {
            if (_owner is null || Context is not { } context) return;
            var dates = _owner.Dates;
            float dayWidth = _owner.DayWidthPx(Width);
            _owner.DrawColumnShades(canvas, 0, Height, dayWidth);
            var today = _owner.Today();
            var culture = _owner._settings?.CultureInfo ?? System.Globalization.CultureInfo.CurrentCulture;
            for (int index = 0; index < dates.Count; index++)
            {
                var date = dates[index];
                float centerX = _owner.GutterPx + index * dayWidth + dayWidth / 2;
                bool isToday = date == today;
                var secondary = WinoCalendarSurfaceGeometry.IsShadedWeekend(date, dates.Count) ? WinoColors.TertiaryText(context) : WinoColors.SecondaryText(context);
                _owner.DrawCenteredLabel(canvas, culture.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek), centerX, Height * 0.3f,
                    isToday ? WinoColors.AccentText(context) : secondary, Resource.Dimension.wino_calendar_day_name_text, false);
                _owner.DrawCenteredLabel(canvas, date.Day.ToString(culture), centerX, Height * 0.68f,
                    isToday ? WinoColors.AccentText(context) : WinoColors.PrimaryText(context), Resource.Dimension.wino_calendar_day_number_text, true);
            }
            _owner.DrawGridLine(canvas, 0, Height - 1, Width, Height - 1, Resource.Color.wino_calendar_grid_line);
        }
    }

    // ---- All-day lanes ----

    private sealed class AllDayView : View
    {
        private readonly WinoCalendarSurfaceView _owner;
        private readonly GestureDetector _gestures;
        private List<CalendarLayoutPlacement> _placements = [];

        public AllDayView(Context context, WinoCalendarSurfaceView owner) : base(context)
        {
            _owner = owner;
            _gestures = new GestureDetector(context, new TapListener(this));
        }

        private AllDayView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { _owner = null!; _gestures = null!; }

        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            int lanes = _owner is null ? 0 : CalendarSurfaceLayoutCalculator.GetAllDayLaneCount(_owner.Dates, _owner.Snapshot());
            int height = (int)Math.Ceiling(CalendarSurfaceLayoutCalculator.GetAllDayHeight(lanes) * (_owner?.Density ?? 1));
            SetMeasuredDimension(MeasureSpec.GetSize(widthMeasureSpec), height);
        }

        protected override void OnDraw(Canvas canvas)
        {
            if (_owner is null || Height == 0) return;
            float density = _owner.Density;
            float dayWidth = _owner.DayWidthPx(Width);
            _owner.DrawColumnShades(canvas, 0, Height, dayWidth);
            _placements = CalendarSurfaceLayoutCalculator.CalculateAllDay(_owner.Dates, _owner.Snapshot(), dayWidth / density);
            foreach (var placement in _placements)
            {
                var b = placement.Bounds;
                var rect = new RectF(_owner.GutterPx + (float)b.X * density, (float)b.Y * density, _owner.GutterPx + (float)b.Right * density, (float)b.Bottom * density);
                _owner.DrawTile(canvas, placement.Item, placement.Date, rect, placement.Kind);
            }
            _owner.DrawGridLine(canvas, 0, Height - 1, Width, Height - 1, Resource.Color.wino_calendar_grid_line);
        }

        public override bool OnTouchEvent(MotionEvent? e) => e is not null && _gestures.OnTouchEvent(e) || base.OnTouchEvent(e);

        private ICalendarItem? HitTest(float x, float y)
        {
            float density = _owner.Density;
            double cx = (x - _owner.GutterPx) / density, cy = y / density;
            return _placements.LastOrDefault(placement => placement.Bounds.Contains(cx, cy))?.Item;
        }

        private sealed class TapListener(AllDayView view) : GestureDetector.SimpleOnGestureListener
        {
            public override bool OnDown(MotionEvent e) => true;

            public override bool OnSingleTapUp(MotionEvent e)
            {
                var owner = view._owner;
                if (view.HitTest(e.GetX(), e.GetY()) is { } item) owner.ItemClicked?.Invoke(owner, new WinoCalendarItemEventArgs(item, view, e.GetX(), e.GetY()));
                else if (owner.Dates.Count > 0 && e.GetX() >= owner.GutterPx)
                {
                    int index = WinoCalendarSurfaceGeometry.DayIndexAt((e.GetX() - owner.GutterPx) / owner.Density + WinoCalendarSurfaceGeometry.HourColumnWidth, owner.Dates.Count, owner.DayWidthPx(view.Width) / owner.Density);
                    if (index >= 0) owner.SlotClicked?.Invoke(owner, new WinoCalendarSlotEventArgs(owner.Dates[index].ToDateTime(TimeOnly.MinValue), true));
                }
                return true;
            }

            public override void OnLongPress(MotionEvent e)
            {
                if (view.HitTest(e.GetX(), e.GetY()) is { } item) view._owner.ItemLongClicked?.Invoke(view._owner, new WinoCalendarItemEventArgs(item, view, e.GetX(), e.GetY()));
            }
        }
    }

    // ---- 24-hour grid ----

    private sealed class TimedView : View
    {
        private readonly WinoCalendarSurfaceView _owner;
        private readonly GestureDetector _gestures;
        private List<CalendarLayoutPlacement> _placements = [];

        public TimedView(Context context, WinoCalendarSurfaceView owner) : base(context)
        {
            _owner = owner;
            _gestures = new GestureDetector(context, new TapListener(this));
        }

        private TimedView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { _owner = null!; _gestures = null!; }

        private float PaddingPx => (float)WinoCalendarSurfaceGeometry.TimelinePadding * _owner.Density;
        private float HourPx => (float)_owner.HourHeightDp * _owner.Density;

        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            int height = _owner is null ? 0 : (int)Math.Ceiling(WinoCalendarSurfaceGeometry.CanvasHeight(_owner.HourHeightDp) * _owner.Density);
            SetMeasuredDimension(MeasureSpec.GetSize(widthMeasureSpec), height);
        }

        protected override void OnLayout(bool changed, int left, int top, int right, int bottom)
        {
            base.OnLayout(changed, left, top, right, bottom);
            if (_owner is null || _owner._scrolledToStart || _owner._scroll.Height == 0) return;
            _owner._scrolledToStart = true;
            double startHour = _owner._settings?.WorkingHourStart.TotalHours ?? 8;
            var offset = WinoCalendarSurfaceGeometry.InitialScrollOffset(startHour, _owner.HourHeightDp, WinoCalendarSurfaceGeometry.CanvasHeight(_owner.HourHeightDp), _owner._scroll.Height / _owner.Density);
            _owner._scroll.Post(() => _owner._scroll.ScrollTo(0, (int)(offset * _owner.Density)));
        }

        protected override void OnDraw(Canvas canvas)
        {
            if (_owner is null || Context is not { } context) return;
            float dayWidth = _owner.DayWidthPx(Width);
            float padding = PaddingPx, hour = HourPx, gutter = _owner.GutterPx;
            _owner.DrawColumnShades(canvas, 0, Height, dayWidth);

            for (int h = 0; h <= 24; h++)
            {
                float y = padding + h * hour;
                _owner.DrawGridLine(canvas, gutter, y, Width, y, Resource.Color.wino_calendar_grid_line);
                if (h < 24)
                {
                    _owner.DrawGridLine(canvas, gutter, y + hour / 2, Width, y + hour / 2, Resource.Color.wino_calendar_half_hour_line);
                    if (h > 0)
                    {
                        string label = _owner._settings?.GetTimedHourLabelText(h) ?? h.ToString(System.Globalization.CultureInfo.CurrentCulture);
                        _owner.DrawCenteredLabel(canvas, label, gutter / 2, y, WinoColors.SecondaryText(context), Resource.Dimension.wino_calendar_hour_label_text, false);
                    }
                }
            }
            for (int index = 0; index <= _owner.Dates.Count; index++)
                _owner.DrawGridLine(canvas, gutter + index * dayWidth, 0, gutter + index * dayWidth, Height, Resource.Color.wino_calendar_grid_line);

            float density = _owner.Density;
            _placements = CalendarSurfaceLayoutCalculator.CalculateTimed(_owner.Dates, _owner.Snapshot(), dayWidth / density, _owner.HourHeightDp,
                _owner._settings?.EventDisplayMode ?? CalendarEventDisplayMode.Stacked);
            foreach (var placement in _placements.OrderBy(p => p.Kind == CalendarPlacementKind.MultiDayGhost ? 0 : 1))
            {
                var b = placement.Bounds;
                var rect = new RectF(gutter + (float)b.X * density, padding + (float)b.Y * density, gutter + (float)b.Right * density, padding + (float)b.Bottom * density);
                _owner.DrawTile(canvas, placement.Item, placement.Date, rect, placement.Kind);
            }

            // The current time on today's column.
            var now = DateTime.Now;
            int todayIndex = WinoCalendarSurfaceGeometry.IndexOf(_owner.Dates, DateOnly.FromDateTime(now));
            if (todayIndex >= 0)
            {
                float y = padding + (float)now.TimeOfDay.TotalHours * hour;
                float x = gutter + todayIndex * dayWidth;
                float line = WinoTokens.PxF(context, Resource.Dimension.wino_calendar_now_line);
                FillPaint.SetStyle(Paint.Style.Fill);
                FillPaint.Color = WinoTokens.Color(context, Resource.Color.wino_calendar_now);
                canvas.DrawRect(x, y - line / 2, x + dayWidth, y + line / 2, FillPaint);
                canvas.DrawCircle(x, y, WinoTokens.PxF(context, Resource.Dimension.wino_calendar_now_dot) / 2, FillPaint);
            }
        }

        public override bool OnTouchEvent(MotionEvent? e) => e is not null && _gestures.OnTouchEvent(e) || base.OnTouchEvent(e);

        private ICalendarItem? HitTest(float x, float y)
        {
            float density = _owner.Density;
            double cx = (x - _owner.GutterPx) / density, cy = (y - PaddingPx) / density;
            return _placements.LastOrDefault(p => p.Kind != CalendarPlacementKind.MultiDayGhost && p.Bounds.Contains(cx, cy))?.Item;
        }

        private sealed class TapListener(TimedView view) : GestureDetector.SimpleOnGestureListener
        {
            public override bool OnDown(MotionEvent e) => true;

            public override bool OnSingleTapUp(MotionEvent e)
            {
                var owner = view._owner;
                if (view.HitTest(e.GetX(), e.GetY()) is { } item)
                {
                    owner.ItemClicked?.Invoke(owner, new WinoCalendarItemEventArgs(item, view, e.GetX(), e.GetY()));
                    return true;
                }
                float density = owner.Density;
                var start = WinoCalendarSurfaceGeometry.SlotStartAt(e.GetX() / density, e.GetY() / density, owner.Dates, owner.DayWidthPx(view.Width) / density, owner.HourHeightDp);
                if (start is { } slot) owner.SlotClicked?.Invoke(owner, new WinoCalendarSlotEventArgs(slot, false));
                return true;
            }

            public override void OnLongPress(MotionEvent e)
            {
                if (view.HitTest(e.GetX(), e.GetY()) is { } item) view._owner.ItemLongClicked?.Invoke(view._owner, new WinoCalendarItemEventArgs(item, view, e.GetX(), e.GetY()));
            }
        }
    }

    // ---- Month grid ----

    private sealed class MonthView : View
    {
        private readonly WinoCalendarSurfaceView _owner;
        private readonly GestureDetector _gestures;
        private List<CalendarLayoutMonthCell> _cells = [];
        private List<CalendarLayoutPlacement> _placements = [];

        public MonthView(Context context, WinoCalendarSurfaceView owner) : base(context)
        {
            _owner = owner;
            _gestures = new GestureDetector(context, new TapListener(this));
        }

        private MonthView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { _owner = null!; _gestures = null!; }

        private float HeaderPx => (float)WinoCalendarSurfaceGeometry.MonthHeaderHeight * _owner.Density;

        protected override void OnDraw(Canvas canvas)
        {
            if (_owner is null || Context is not { } context || _owner._range is not { } range) return;
            float density = _owner.Density, header = HeaderPx;
            var settings = _owner._settings;
            var culture = settings?.CultureInfo ?? System.Globalization.CultureInfo.CurrentCulture;
            var dates = CalendarSurfaceLayoutCalculator.MonthGridDates(range.StartDate, settings?.FirstDayOfWeek ?? culture.DateTimeFormat.FirstDayOfWeek);
            float cellWidth = Width / (float)CalendarSurfaceLayoutCalculator.MonthColumns;
            for (int col = 0; col < CalendarSurfaceLayoutCalculator.MonthColumns; col++)
                _owner.DrawCenteredLabel(canvas, culture.DateTimeFormat.GetAbbreviatedDayName(dates[col].DayOfWeek), cellWidth * col + cellWidth / 2, header / 2,
                    WinoColors.SecondaryText(context), Resource.Dimension.wino_calendar_day_name_text, false);

            var items = _owner.Snapshot();
            (_cells, _placements) = CalendarSurfaceLayoutCalculator.CalculateMonth(dates, items, Width / density, (Height - header) / density);
            // Phone-width cells show only the day's event count (owner, 15 Oct 2026; the iPhone surface): titles are unreadable there.
            bool countOnly = cellWidth < WinoTokens.PxF(context, Resource.Dimension.wino_calendar_month_count_only_width);
            if (countOnly) _placements = [];
            var today = _owner.Today();
            foreach (var cell in _cells)
            {
                var b = cell.Bounds;
                float left = (float)b.X * density, top = header + (float)b.Y * density;
                if (cell.Date == today)
                {
                    FillPaint.SetStyle(Paint.Style.Fill);
                    FillPaint.Color = WinoColors.CalendarTodayColumn(context);
                    canvas.DrawRect(left, top, left + (float)b.Width * density, top + (float)b.Height * density, FillPaint);
                }
                _owner.DrawGridLine(canvas, left, top, left + (float)b.Width * density, top, Resource.Color.wino_calendar_grid_line);
                _owner.DrawGridLine(canvas, left, top, left, top + (float)b.Height * density, Resource.Color.wino_calendar_grid_line);
                bool inRange = range.Contains(cell.Date);
                var color = cell.Date == today ? WinoColors.AccentText(context) : inRange ? WinoColors.PrimaryText(context) : WinoColors.TertiaryText(context);
                _owner.DrawCenteredLabel(canvas, cell.Date.Day.ToString(culture), left + (float)CalendarSurfaceLayoutCalculator.MonthCellPadding * density + cellWidth / 8,
                    top + (float)CalendarSurfaceLayoutCalculator.MonthDayLabelHeight * density / 2 + (float)CalendarSurfaceLayoutCalculator.MonthCellPadding * density / 2,
                    color, Resource.Dimension.wino_calendar_tile_title_text, cell.Date == today);
                if (countOnly) DrawCount(canvas, context, CountOn(items, cell.Date), left, top + (float)CalendarSurfaceLayoutCalculator.MonthDayLabelHeight * density,
                    (float)b.Width * density, top + (float)b.Height * density, inRange);
            }
            foreach (var placement in _placements)
            {
                var b = placement.Bounds;
                var rect = new RectF((float)b.X * density, header + (float)b.Y * density, (float)b.Right * density, header + (float)b.Bottom * density);
                _owner.DrawTile(canvas, placement.Item, placement.Date, rect, placement.Kind);
            }
        }

        /// <summary>Events that touch <paramref name="date"/> (an all-day event's exclusive end does not count).</summary>
        private static int CountOn(List<ICalendarItem> items, DateOnly date)
        {
            var start = date.ToDateTime(TimeOnly.MinValue);
            var end = start.AddDays(1);
            return items.Count(item => item.StartDate < end && (item.EndDate > start || (item.EndDate <= item.StartDate && item.StartDate >= start)));
        }

        /// <summary>The count pill, centred in the cell under the day number (the phone month grid's pill tokens).</summary>
        private static void DrawCount(Canvas canvas, Context context, int count, float left, float top, float width, float bottom, bool inRange)
        {
            if (count <= 0) return;
            string text = count > 99 ? "99+" : count.ToString(System.Globalization.CultureInfo.CurrentCulture);
            LabelPaint.TextAlign = Paint.Align.Center;
            LabelPaint.TextSize = WinoTokens.PxF(context, Resource.Dimension.wino_calendar_month_count_text);
            LabelPaint.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
            float height = WinoTokens.PxF(context, Resource.Dimension.wino_calendar_month_count_height);
            float pillWidth = Math.Max(WinoTokens.PxF(context, Resource.Dimension.wino_calendar_month_count_min_width),
                LabelPaint.MeasureText(text) + 2 * WinoTokens.PxF(context, Resource.Dimension.wino_calendar_month_count_padding));
            float centerX = left + width / 2;
            float centerY = top + Math.Max(height / 2, (bottom - top) / 2);
            var accent = WinoColors.AccentText(context);
            FillPaint.SetStyle(Paint.Style.Fill);
            FillPaint.Color = WinoColors.WithAlpha(accent, inRange ? 0.16 : 0.08);
            canvas.DrawRoundRect(new RectF(centerX - pillWidth / 2, centerY - height / 2, centerX + pillWidth / 2, centerY + height / 2), height / 2, height / 2, FillPaint);
            LabelPaint.Color = inRange ? accent : WinoColors.WithAlpha(accent, 0.5);
            var metrics = LabelPaint.GetFontMetrics();
            canvas.DrawText(text, centerX, centerY - ((metrics?.Ascent ?? 0) + (metrics?.Descent ?? 0)) / 2, LabelPaint);
        }

        public override bool OnTouchEvent(MotionEvent? e) => e is not null && _gestures.OnTouchEvent(e) || base.OnTouchEvent(e);

        private sealed class TapListener(MonthView view) : GestureDetector.SimpleOnGestureListener
        {
            public override bool OnDown(MotionEvent e) => true;

            private (ICalendarItem? Item, DateOnly? Date) Hit(MotionEvent e)
            {
                float density = view._owner.Density;
                double x = e.GetX() / density, y = (e.GetY() - view.HeaderPx) / density;
                var item = view._placements.LastOrDefault(p => p.Bounds.Contains(x, y))?.Item;
                var date = view._cells.FirstOrDefault(c => c.Bounds.Contains(x, y))?.Date;
                return (item, date);
            }

            public override bool OnSingleTapUp(MotionEvent e)
            {
                var owner = view._owner;
                var (item, date) = Hit(e);
                if (item is not null) owner.ItemClicked?.Invoke(owner, new WinoCalendarItemEventArgs(item, view, e.GetX(), e.GetY()));
                else if (date is { } day) owner.SlotClicked?.Invoke(owner, new WinoCalendarSlotEventArgs(day.ToDateTime(TimeOnly.MinValue), true));
                return true;
            }

            public override void OnLongPress(MotionEvent e)
            {
                if (Hit(e).Item is { } item) view._owner.ItemLongClicked?.Invoke(view._owner, new WinoCalendarItemEventArgs(item, view, e.GetX(), e.GetY()));
            }
        }
    }
}
