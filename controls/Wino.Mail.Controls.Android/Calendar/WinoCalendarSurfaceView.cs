using System.Collections.Specialized;
using Android.Content;
using Android.Graphics;
using Google.Android.Material.Dialog;
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
/// The day columns and month pages sit on an infinite rail that follows the finger (WinoCalendarSurfaceView.Rail,
/// docs/calendar-rail-navigation.md) and raises <see cref="PeriodSettled"/> when it rests. Placement comes from the shared
/// <see cref="CalendarSurfaceLayoutCalculator"/> and <see cref="WinoCalendarSurfaceGeometry"/>, tile colours from
/// <see cref="WinoCalendarTileAppearance"/>. Taps raise <see cref="ItemClicked"/> or <see cref="SlotClicked"/>, a long press
/// on a tile <see cref="ItemLongClicked"/>. Dragging tiles to move or resize them is not implemented on Android.
/// </summary>
[Register("com.winomail.controls.WinoCalendarSurfaceView")]
public sealed partial class WinoCalendarSurfaceView : LinearLayout
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
            ApplyRange();
        }
    }

    public CalendarSettings? Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            _scrolledToStart = false;
            ApplyRange();
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

    /// <summary>True while the 24-hour grid can scroll up or the rail pans (pull to refresh waits for its top and never takes a pan).</summary>
    public bool CanScrollUp => _isPanning || (_scroll.Visibility == ViewStates.Visible && _scroll.CanScrollVertically(-1));

    /// <summary>Lays the tiles out again (items moved or the range changed).</summary>
    public void ReloadLayout()
    {
        _layoutVersion++;
        _allDay.RequestLayout();
        _timed.RequestLayout();
        RefreshTiles();
    }

    /// <summary>Redraws the tiles (selection or titles changed).</summary>
    public void RefreshTiles()
    {
        _layoutVersion++;
        InvalidateStrips();
    }

    /// <summary>Redraws every strip; their recorded layers replay at the new rail offset.</summary>
    private void InvalidateStrips()
    {
        _header.Invalidate();
        _allDay.Invalidate();
        _timed.Invalidate();
        _month.Invalidate();
    }

    /// <summary>
    /// A drawing recorded once and replayed every frame of rail motion; it is recorded again only when the layout version
    /// or its size changes, so a frame of motion costs one picture replay instead of every label and tile.
    /// </summary>
    private sealed class RecordedLayer
    {
        private Picture? _picture;
        private int _version = -1;
        private int _width;
        private int _height;

        public void Draw(Canvas canvas, int version, int width, int height, Action<Canvas> record)
            => canvas.DrawPicture(Prepare(version, width, height, record));

        /// <summary>Records the drawing when it is missing or stale, so a later frame only replays it.</summary>
        public Picture Prepare(int version, int width, int height, Action<Canvas> record)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (_picture is not null && version == _version && width == _width && height == _height) return _picture;
            _picture?.Dispose();
            var picture = new Picture();
            record(picture.BeginRecording(width, height)!);
            picture.EndRecording();
            _picture = picture;
            _version = version;
            _width = width;
            _height = height;
            return picture;
        }
    }

    protected override void OnDetachedFromWindow()
    {
        StopAllMotion();
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

    /// <summary>The realized day columns of the rail (Day, Week and Work week).</summary>
    private IReadOnlyList<DateOnly> Dates => _windowDates;
    private List<ICalendarItem> Snapshot() => [.. _items];

    private float Density => WinoUnits.Density(Context!);

    /// <summary>The width of the realized columns, the extent of a recorded rail layer.</summary>
    private int RailWidthPx => (int)Math.Ceiling(Dates.Count * UnitPx) + 1;
    private float GutterPx => (float)WinoCalendarSurfaceGeometry.HourColumnWidth * Density;
    private double HourHeightDp => WinoCalendarSurfaceGeometry.HourHeight(_settings?.HourHeight ?? 0);

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

    /// <summary>The "n more events" link (Windows CalendarMoreEventsButton): accent medium text, left aligned in its row.</summary>
    private void DrawMoreLink(Canvas canvas, Context context, string text, RectF bounds)
    {
        LabelPaint.TextAlign = Paint.Align.Left;
        LabelPaint.TextSize = WinoTokens.PxF(context, Resource.Dimension.wino_calendar_tile_title_text);
        LabelPaint.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
        LabelPaint.Color = WinoColors.AccentText(context);
        float padding = 4 * Density;
        var shown = TextUtils.Ellipsize(text, LabelPaint, Math.Max(0, bounds.Width() - padding * 2), TextUtils.TruncateAt.End) ?? text;
        var metrics = LabelPaint.GetFontMetrics();
        float baseline = bounds.CenterY() - ((metrics?.Ascent ?? 0) + (metrics?.Descent ?? 0)) / 2;
        canvas.DrawText(shown, bounds.Left + padding, baseline, LabelPaint);
    }

    /// <summary>The hidden events of a day, picked from a list (the Windows and Mac "n more" flyout); a pick is an item click.</summary>
    private void ShowOverflow(View anchor, CalendarLayoutOverflowCell overflow, float x, float y)
    {
        if (Context is not { } context || overflow.HiddenItems.Count == 0) return;
        var items = overflow.HiddenItems;
        var culture = _settings?.CultureInfo ?? System.Globalization.CultureInfo.CurrentCulture;
        new MaterialAlertDialogBuilder(context)
            .SetTitle(overflow.Date.ToDateTime(TimeOnly.MinValue).ToString("D", culture))
            .SetItems(items.Select(item => item.Title ?? string.Empty).ToArray(), (_, args) =>
            {
                if (args.Which >= 0 && args.Which < items.Count)
                    ItemClicked?.Invoke(this, new WinoCalendarItemEventArgs(items[args.Which], anchor, x, y));
            })
            .Show();
    }

    private void DrawCenteredLabel(Canvas canvas, string text, float x, float centerY, Color color, int sizeResource, bool medium)
    {
        LabelPaint.Color = color;
        LabelPaint.TextAlign = Paint.Align.Center;
        LabelPaint.TextSize = WinoTokens.PxF(Context!, sizeResource);
        LabelPaint.SetTypeface(Typeface.Create(medium ? "sans-serif-medium" : "sans-serif", TypefaceStyle.Normal));
        var metrics = LabelPaint.GetFontMetrics();
        canvas.DrawText(text, x, centerY - ((metrics?.Ascent ?? 0) + (metrics?.Descent ?? 0)) / 2, LabelPaint);
    }

    /// <summary>Today's column and the weekends of the realized columns, in rail coordinates (column 0 at x = 0).</summary>
    private void DrawColumnShades(Canvas canvas, float top, float bottom, float dayWidth)
    {
        var context = Context!;
        var today = Today();
        for (int index = 0; index < Dates.Count; index++)
        {
            var date = Dates[index];
            float left = index * dayWidth;
            if (date == today) FillPaint.Color = WinoColors.CalendarTodayColumn(context);
            else if (WinoCalendarSurfaceGeometry.IsShadedWeekend(date, VisibleUnitCount)) FillPaint.Color = WinoTokens.Color(context, Resource.Color.wino_calendar_weekend);
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
        private readonly RecordedLayer _rail = new();

        public DayHeaderView(Context context, WinoCalendarSurfaceView owner) : base(context) => _owner = owner;
        private DayHeaderView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) => _owner = null!;

        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
            => SetMeasuredDimension(MeasureSpec.GetSize(widthMeasureSpec), WinoTokens.Px(Context!, Resource.Dimension.wino_calendar_surface_day_header_height));

        protected override void OnDraw(Canvas canvas)
        {
            if (_owner is null || Context is not { } context) return;
            canvas.Save();
            canvas.ClipRect(_owner.GutterPx, 0, Width, Height);
            canvas.Translate(_owner.ColumnLeft(0), 0);
            _rail.Draw(canvas, _owner._layoutVersion, _owner.RailWidthPx, Height, layer => DrawColumns(layer, context));
            canvas.Restore();
            _owner.DrawGridLine(canvas, 0, Height - 1, Width, Height - 1, Resource.Color.wino_calendar_grid_line);
        }

        /// <summary>The weekday and number of every realized column, in rail coordinates.</summary>
        private void DrawColumns(Canvas canvas, Context context)
        {
            var dates = _owner.Dates;
            float dayWidth = _owner.UnitPx;
            _owner.DrawColumnShades(canvas, 0, Height, dayWidth);
            var today = _owner.Today();
            var culture = _owner._settings?.CultureInfo ?? System.Globalization.CultureInfo.CurrentCulture;
            for (int index = 0; index < dates.Count; index++)
            {
                var date = dates[index];
                float centerX = index * dayWidth + dayWidth / 2;
                bool isToday = date == today;
                var secondary = WinoCalendarSurfaceGeometry.IsShadedWeekend(date, _owner.VisibleUnitCount) ? WinoColors.TertiaryText(context) : WinoColors.SecondaryText(context);
                _owner.DrawCenteredLabel(canvas, culture.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek), centerX, Height * 0.3f,
                    isToday ? WinoColors.AccentText(context) : secondary, Resource.Dimension.wino_calendar_day_name_text, false);
                _owner.DrawCenteredLabel(canvas, date.Day.ToString(culture), centerX, Height * 0.68f,
                    isToday ? WinoColors.AccentText(context) : WinoColors.PrimaryText(context), Resource.Dimension.wino_calendar_day_number_text, true);
            }
        }
    }

    // ---- All-day lanes ----

    private sealed class AllDayView : View
    {
        private readonly WinoCalendarSurfaceView _owner;
        private readonly GestureDetector _gestures;
        private readonly RecordedLayer _rail = new();
        private List<CalendarLayoutPlacement> _placements = [];
        private List<CalendarLayoutOverflowCell> _overflows = [];
        private int _placementsVersion = -1;
        private float _placementsUnit;

        public AllDayView(Context context, WinoCalendarSurfaceView owner) : base(context)
        {
            _owner = owner;
            _gestures = new GestureDetector(context, new TapListener(this));
        }

        private AllDayView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { _owner = null!; _gestures = null!; }

        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            var (lanes, hasOverflow) = _owner is null ? (0, false) : CalendarSurfaceLayoutCalculator.GetAllDayLaneCount(_owner.Dates, _owner.Snapshot());
            int height = (int)Math.Ceiling(CalendarSurfaceLayoutCalculator.GetAllDayHeight(lanes, hasOverflow) * (_owner?.Density ?? 1));
            SetMeasuredDimension(MeasureSpec.GetSize(widthMeasureSpec), height);
        }

        protected override void OnDraw(Canvas canvas)
        {
            if (_owner is null || Height == 0) return;
            float density = _owner.Density;
            float dayWidth = _owner.UnitPx;
            if (_placementsVersion != _owner._layoutVersion || _placementsUnit != dayWidth)
            {
                (_placements, _overflows) = CalendarSurfaceLayoutCalculator.CalculateAllDay(_owner.Dates, _owner.Snapshot(), dayWidth / density);
                _placementsVersion = _owner._layoutVersion;
                _placementsUnit = dayWidth;
            }
            canvas.Save();
            canvas.ClipRect(_owner.GutterPx, 0, Width, Height);
            canvas.Translate(_owner.ColumnLeft(0), 0);
            _rail.Draw(canvas, _owner._layoutVersion, _owner.RailWidthPx, Height, layer =>
            {
                _owner.DrawColumnShades(layer, 0, Height, dayWidth);
                foreach (var placement in _placements)
                {
                    var b = placement.Bounds;
                    _owner.DrawTile(layer, placement.Item, placement.Date, new RectF((float)b.X * density, (float)b.Y * density, (float)b.Right * density, (float)b.Bottom * density), placement.Kind);
                }
                if (Context is { } context)
                {
                    foreach (var overflow in _overflows)
                    {
                        var b = overflow.Bounds;
                        _owner.DrawMoreLink(layer, context, overflow.Text, new RectF((float)b.X * density, (float)b.Y * density, (float)b.Right * density, (float)b.Bottom * density));
                    }
                }
            });
            canvas.Restore();
            _owner.DrawGridLine(canvas, 0, Height - 1, Width, Height - 1, Resource.Color.wino_calendar_grid_line);
        }

        public override bool OnTouchEvent(MotionEvent? e) => e is not null && _gestures.OnTouchEvent(e) || base.OnTouchEvent(e);

        private ICalendarItem? HitTest(float x, float y)
        {
            if (x < _owner.GutterPx) return null;
            float density = _owner.Density;
            double cx = (x - _owner.ColumnLeft(0)) / density, cy = y / density;
            return _placements.LastOrDefault(placement => placement.Bounds.Contains(cx, cy))?.Item;
        }

        private CalendarLayoutOverflowCell? OverflowAt(float x, float y)
        {
            if (x < _owner.GutterPx) return null;
            float density = _owner.Density;
            double cx = (x - _owner.ColumnLeft(0)) / density, cy = y / density;
            return _overflows.FirstOrDefault(overflow => overflow.Bounds.Contains(cx, cy));
        }

        private sealed class TapListener(AllDayView view) : GestureDetector.SimpleOnGestureListener
        {
            public override bool OnDown(MotionEvent e) => true;

            public override bool OnSingleTapUp(MotionEvent e)
            {
                var owner = view._owner;
                if (view.HitTest(e.GetX(), e.GetY()) is { } item) owner.ItemClicked?.Invoke(owner, new WinoCalendarItemEventArgs(item, view, e.GetX(), e.GetY()));
                else if (view.OverflowAt(e.GetX(), e.GetY()) is { } overflow) owner.ShowOverflow(view, overflow, e.GetX(), e.GetY());
                else if (owner.ColumnAt(e.GetX()) is var index and >= 0)
                {
                    owner.SlotClicked?.Invoke(owner, new WinoCalendarSlotEventArgs(owner.Dates[index].ToDateTime(TimeOnly.MinValue), true));
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
        private readonly RecordedLayer _shades = new();
        private readonly RecordedLayer _hours = new();
        private readonly RecordedLayer _rail = new();
        private List<CalendarLayoutPlacement> _placements = [];
        private int _placementsVersion = -1;
        private float _placementsUnit;

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
            float dayWidth = _owner.UnitPx;
            float padding = PaddingPx, hour = HourPx, gutter = _owner.GutterPx;
            float density = _owner.Density;
            if (_placementsVersion != _owner._layoutVersion || _placementsUnit != dayWidth)
            {
                _placements = CalendarSurfaceLayoutCalculator.CalculateTimed(_owner.Dates, _owner.Snapshot(), dayWidth / density, _owner.HourHeightDp,
                    _owner._settings?.EventDisplayMode ?? CalendarEventDisplayMode.Stacked)
                    .OrderBy(p => p.Kind == CalendarPlacementKind.MultiDayGhost ? 0 : 1).ToList();
                _placementsVersion = _owner._layoutVersion;
                _placementsUnit = dayWidth;
            }

            // The realized columns move with the rail (shading under the hour lines, lines and tiles over them); the
            // gutter, its hour labels and the hour lines stay.
            int version = _owner._layoutVersion;
            canvas.Save();
            canvas.ClipRect(gutter, 0, Width, Height);
            canvas.Translate(_owner.ColumnLeft(0), 0);
            _shades.Draw(canvas, version, _owner.RailWidthPx, Height, layer => _owner.DrawColumnShades(layer, 0, Height, dayWidth));
            canvas.Restore();

            _hours.Draw(canvas, version, Width, Height, layer =>
            {
                for (int h = 0; h <= 24; h++)
                {
                    float y = padding + h * hour;
                    _owner.DrawGridLine(layer, gutter, y, Width, y, Resource.Color.wino_calendar_grid_line);
                    if (h < 24)
                    {
                        _owner.DrawGridLine(layer, gutter, y + hour / 2, Width, y + hour / 2, Resource.Color.wino_calendar_half_hour_line);
                        if (h > 0)
                        {
                            string label = _owner._settings?.GetTimedHourLabelText(h) ?? h.ToString(System.Globalization.CultureInfo.CurrentCulture);
                            _owner.DrawCenteredLabel(layer, label, gutter / 2, y, WinoColors.SecondaryText(context), Resource.Dimension.wino_calendar_hour_label_text, false);
                        }
                    }
                }
            });

            canvas.Save();
            canvas.ClipRect(gutter, 0, Width, Height);
            canvas.Translate(_owner.ColumnLeft(0), 0);
            _rail.Draw(canvas, version, _owner.RailWidthPx, Height, layer =>
            {
                for (int index = 0; index <= _owner.Dates.Count; index++)
                    _owner.DrawGridLine(layer, index * dayWidth, 0, index * dayWidth, Height, Resource.Color.wino_calendar_grid_line);
                foreach (var placement in _placements)
                {
                    var b = placement.Bounds;
                    var rect = new RectF((float)b.X * density, padding + (float)b.Y * density, (float)b.Right * density, padding + (float)b.Bottom * density);
                    _owner.DrawTile(layer, placement.Item, placement.Date, rect, placement.Kind);
                }
            });

            // The current time on today's column (drawn live, so it keeps moving).
            var now = DateTime.Now;
            int todayIndex = WinoCalendarSurfaceGeometry.IndexOf(_owner.Dates, DateOnly.FromDateTime(now));
            if (todayIndex >= 0)
            {
                float y = padding + (float)now.TimeOfDay.TotalHours * hour;
                float x = todayIndex * dayWidth;
                float line = WinoTokens.PxF(context, Resource.Dimension.wino_calendar_now_line);
                FillPaint.SetStyle(Paint.Style.Fill);
                FillPaint.Color = WinoTokens.Color(context, Resource.Color.wino_calendar_now);
                canvas.DrawRect(x, y - line / 2, x + dayWidth, y + line / 2, FillPaint);
                canvas.DrawCircle(x, y, WinoTokens.PxF(context, Resource.Dimension.wino_calendar_now_dot) / 2, FillPaint);
            }
            canvas.Restore();
        }

        public override bool OnTouchEvent(MotionEvent? e) => e is not null && _gestures.OnTouchEvent(e) || base.OnTouchEvent(e);

        private ICalendarItem? HitTest(float x, float y)
        {
            if (x < _owner.GutterPx) return null;
            float density = _owner.Density;
            double cx = (x - _owner.ColumnLeft(0)) / density, cy = (y - PaddingPx) / density;
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
                if (owner.ColumnAt(e.GetX()) is var index and >= 0)
                {
                    int slot = WinoCalendarSurfaceGeometry.SlotAt(e.GetY() / owner.Density, owner.HourHeightDp);
                    var start = owner.Dates[index].ToDateTime(TimeOnly.MinValue).AddMinutes(slot * WinoCalendarSurfaceGeometry.GridIntervalMinutes);
                    owner.SlotClicked?.Invoke(owner, new WinoCalendarSlotEventArgs(start, false));
                }
                return true;
            }

            public override void OnLongPress(MotionEvent e)
            {
                if (view.HitTest(e.GetX(), e.GetY()) is { } item) view._owner.ItemLongClicked?.Invoke(view._owner, new WinoCalendarItemEventArgs(item, view, e.GetX(), e.GetY()));
            }
        }
    }

    // ---- Month grid ----

    /// <summary>
    /// The month pages: the weekday names stay put while the six-week grids of the realized months move with the rail. Each
    /// page dims the days outside its own month.
    /// </summary>
    private sealed class MonthView : View
    {
        private readonly WinoCalendarSurfaceView _owner;
        private readonly GestureDetector _gestures;
        private readonly Dictionary<int, MonthPage> _pages = [];
        private int _pagesVersion = -1;
        private int _pagesWidth;
        private int _pagesHeight;
        private bool _neighboursPosted;
        private int _preparedCenter = int.MinValue;
        private int _preparedVersion = -1;

        /// <summary>One laid-out month: its cells and tiles (or, where cells are phone-narrow, only its counts).</summary>
        private sealed record MonthPage(DateOnly Month, List<CalendarLayoutMonthCell> Cells, List<CalendarLayoutPlacement> Placements,
            List<CalendarLayoutOverflowCell> Overflows, bool CountOnly)
        {
            /// <summary>The page's drawing, replayed while it moves.</summary>
            public RecordedLayer Layer { get; } = new();
        }

        public MonthView(Context context, WinoCalendarSurfaceView owner) : base(context)
        {
            _owner = owner;
            _gestures = new GestureDetector(context, new TapListener(this));
        }

        private MonthView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { _owner = null!; _gestures = null!; }

        private float HeaderPx => (float)WinoCalendarSurfaceGeometry.MonthHeaderHeight * _owner.Density;

        private DayOfWeek FirstDayOfWeek => _owner._settings?.FirstDayOfWeek ?? Culture.DateTimeFormat.FirstDayOfWeek;

        private System.Globalization.CultureInfo Culture => _owner._settings?.CultureInfo ?? System.Globalization.CultureInfo.CurrentCulture;

        /// <summary>The laid-out page of a month, built on first use after anything changed.</summary>
        private MonthPage Page(Context context, int monthIndex)
        {
            if (_pagesVersion != _owner._layoutVersion || _pagesWidth != Width || _pagesHeight != Height)
            {
                _pages.Clear();
                _pagesVersion = _owner._layoutVersion;
                _pagesWidth = Width;
                _pagesHeight = Height;
            }
            if (_pages.TryGetValue(monthIndex, out var page)) return page;
            float density = _owner.Density;
            var month = MonthStart(monthIndex);
            var dates = CalendarSurfaceLayoutCalculator.MonthGridDates(month, FirstDayOfWeek);
            var (cells, placements, overflows) = CalendarSurfaceLayoutCalculator.CalculateMonth(dates, _owner.Snapshot(), Width / density, (Height - HeaderPx) / density);
            // Phone-width cells show only the day's event count (owner, 15 Oct 2026; the iPhone surface): titles are unreadable there.
            bool countOnly = Width / (float)CalendarSurfaceLayoutCalculator.MonthColumns < WinoTokens.PxF(context, Resource.Dimension.wino_calendar_month_count_only_width);
            page = new MonthPage(month, cells, countOnly ? [] : placements, countOnly ? [] : overflows, countOnly);
            // Panning realizes at most two pages at a time; keep a few for back-and-forth movement.
            if (_pages.Count > 6)
            {
                int center = (int)Math.Round(_owner._position);
                foreach (int stale in _pages.Keys.Where(key => Math.Abs(key - center) > 1).ToList()) _pages.Remove(stale);
            }
            _pages[monthIndex] = page;
            return page;
        }

        protected override void OnDraw(Canvas canvas)
        {
            if (_owner is null || Context is not { } context || !_owner.IsMonthPages || Width == 0) return;
            float header = HeaderPx;
            var culture = Culture;
            var first = FirstDayOfWeek;
            float cellWidth = Width / (float)CalendarSurfaceLayoutCalculator.MonthColumns;
            for (int col = 0; col < CalendarSurfaceLayoutCalculator.MonthColumns; col++)
                _owner.DrawCenteredLabel(canvas, culture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)(((int)first + col) % 7)), cellWidth * col + cellWidth / 2, header / 2,
                    WinoColors.SecondaryText(context), Resource.Dimension.wino_calendar_day_name_text, false);

            double position = _owner._position;
            int firstPage = (int)Math.Floor(position);
            int lastPage = (int)Math.Ceiling(position);
            canvas.Save();
            canvas.ClipRect(0, header, Width, Height);
            for (int monthIndex = firstPage; monthIndex <= lastPage; monthIndex++)
            {
                float left = (float)((monthIndex - position) * Width);
                if (left >= Width || left + Width <= 0) continue;
                canvas.Save();
                canvas.Translate(left, 0);
                var page = Page(context, monthIndex);
                page.Layer.Draw(canvas, _owner._layoutVersion, Width, Height, layer => DrawPage(layer, context, page, page.CountOnly ? _owner.Snapshot() : null, culture));
                canvas.Restore();
            }
            canvas.Restore();
            if (firstPage == lastPage) PrepareNeighboursLater(firstPage);
        }

        /// <summary>
        /// Builds and records the previous and next months once the rail rests, so the first frame of a pan only replays
        /// them instead of laying a month out.
        /// </summary>
        private void PrepareNeighboursLater(int monthIndex)
        {
            if (_neighboursPosted || (_preparedCenter == monthIndex && _preparedVersion == _owner._layoutVersion)) return;
            _neighboursPosted = true;
            Post(() =>
            {
                _neighboursPosted = false;
                if (Context is not { } context || !_owner.IsMonthPages || _owner._isPanning || Width == 0) return;
                var culture = Culture;
                foreach (int neighbour in new[] { monthIndex - 1, monthIndex + 1 })
                {
                    var page = Page(context, neighbour);
                    page.Layer.Prepare(_owner._layoutVersion, Width, Height, layer => DrawPage(layer, context, page, page.CountOnly ? _owner.Snapshot() : null, culture));
                }
                _preparedCenter = monthIndex;
                _preparedVersion = _owner._layoutVersion;
            });
        }

        private void DrawPage(Canvas canvas, Context context, MonthPage page, List<ICalendarItem>? items, System.Globalization.CultureInfo culture)
        {
            float density = _owner.Density, header = HeaderPx;
            float cellWidth = Width / (float)CalendarSurfaceLayoutCalculator.MonthColumns;
            var today = _owner.Today();
            foreach (var cell in page.Cells)
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
                bool inMonth = cell.Date.Year == page.Month.Year && cell.Date.Month == page.Month.Month;
                var color = cell.Date == today ? WinoColors.AccentText(context) : inMonth ? WinoColors.PrimaryText(context) : WinoColors.TertiaryText(context);
                _owner.DrawCenteredLabel(canvas, cell.Date.Day.ToString(culture), left + (float)CalendarSurfaceLayoutCalculator.MonthCellPadding * density + cellWidth / 8,
                    top + (float)CalendarSurfaceLayoutCalculator.MonthDayLabelHeight * density / 2 + (float)CalendarSurfaceLayoutCalculator.MonthCellPadding * density / 2,
                    color, Resource.Dimension.wino_calendar_tile_title_text, cell.Date == today);
                if (page.CountOnly && items is not null) DrawCount(canvas, context, CountOn(items, cell.Date), left, top + (float)CalendarSurfaceLayoutCalculator.MonthDayLabelHeight * density,
                    (float)b.Width * density, top + (float)b.Height * density, inMonth);
            }
            foreach (var placement in page.Placements)
            {
                var b = placement.Bounds;
                var rect = new RectF((float)b.X * density, header + (float)b.Y * density, (float)b.Right * density, header + (float)b.Bottom * density);
                _owner.DrawTile(canvas, placement.Item, placement.Date, rect, placement.Kind);
            }
            foreach (var overflow in page.Overflows)
            {
                var b = overflow.Bounds;
                _owner.DrawMoreLink(canvas, context, overflow.Text, new RectF((float)b.X * density, header + (float)b.Y * density, (float)b.Right * density, header + (float)b.Bottom * density));
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

            /// <summary>The tile, the "n more" link and the day under a press on the page the rail rests on.</summary>
            private (ICalendarItem? Item, DateOnly? Date, CalendarLayoutOverflowCell? Overflow) Hit(MotionEvent e)
            {
                if (view.Context is not { } context || !view._owner.IsMonthPages) return (null, null, null);
                double position = view._owner._position;
                int monthIndex = (int)Math.Round(position);
                var page = view.Page(context, monthIndex);
                float density = view._owner.Density;
                double x = (e.GetX() - (monthIndex - position) * view.Width) / density, y = (e.GetY() - view.HeaderPx) / density;
                var item = page.Placements.LastOrDefault(p => p.Bounds.Contains(x, y))?.Item;
                var overflow = page.Overflows.FirstOrDefault(o => o.Bounds.Contains(x, y));
                var date = page.Cells.FirstOrDefault(c => c.Bounds.Contains(x, y))?.Date;
                return (item, date, overflow);
            }

            public override bool OnSingleTapUp(MotionEvent e)
            {
                var owner = view._owner;
                var (item, date, overflow) = Hit(e);
                if (item is not null) owner.ItemClicked?.Invoke(owner, new WinoCalendarItemEventArgs(item, view, e.GetX(), e.GetY()));
                else if (overflow is not null) owner.ShowOverflow(view, overflow, e.GetX(), e.GetY());
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
