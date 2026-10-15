using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Wino.Mail.Controls.Core.Calendar;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.Calendar;

/// <summary>
/// A seven-column month grid (canvas Material pass board CalendarMonth; the Windows ShellCalendarView date picker in the
/// tablet pane): the weekday initials, then the weeks of <see cref="DisplayMonth"/> from the configured first day
/// (shared <see cref="WinoMiniCalendarGrid"/>; a trailing week of next-month days is left out). Each day is a number in a
/// circle — today filled with the accent, the selected day on the secondary container, days of other months tertiary —
/// with up to three event colour dots below (<see cref="DotsProvider"/>) or, in the pane, the visible range as a band
/// (<see cref="HighlightedDates"/>). Taps raise <see cref="DateClicked"/>; horizontal flings change the month.
/// Usable from XML as <c>com.winomail.controls.WinoCalendarMonthGridView</c>.
/// </summary>
[Register("com.winomail.controls.WinoCalendarMonthGridView")]
public sealed class WinoCalendarMonthGridView : View
{
    private readonly Paint _textPaint = new(PaintFlags.AntiAlias);
    private readonly Paint _fillPaint = new(PaintFlags.AntiAlias);
    private GestureDetector? _gestures;
    private DateOnly _displayMonth = DateOnly.FromDateTime(DateTime.Today);
    private DayOfWeek _firstDayOfWeek = DayOfWeek.Monday;
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly? _selected;
    private IReadOnlySet<DateOnly> _highlighted = new HashSet<DateOnly>();
    private Func<DateOnly, IReadOnlyList<Color>>? _dots;
    private bool _isCompact;
    private System.Globalization.CultureInfo _culture = System.Globalization.CultureInfo.CurrentCulture;

    public WinoCalendarMonthGridView(Context context) : base(context) => Initialize(context);
    public WinoCalendarMonthGridView(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize(context);
    private WinoCalendarMonthGridView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public event EventHandler<DateOnly>? DateClicked;
    public event EventHandler? SwipedNext;
    public event EventHandler? SwipedPrevious;

    private void Initialize(Context context)
    {
        _gestures = new GestureDetector(context, new Gestures(this));
        Clickable = true;
        Focusable = true;
        _textPaint.TextAlign = Paint.Align.Center;
    }

    public DateOnly DisplayMonth
    {
        get => _displayMonth;
        set { _displayMonth = WinoMiniCalendarGrid.MonthStart(value); RequestLayout(); Invalidate(); UpdateDescription(); }
    }

    public DayOfWeek FirstDayOfWeek
    {
        get => _firstDayOfWeek;
        set { _firstDayOfWeek = value; RequestLayout(); Invalidate(); }
    }

    public DateOnly Today
    {
        get => _today;
        set { _today = value; Invalidate(); }
    }

    public DateOnly? SelectedDate
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    /// <summary>Days drawn on the range band (the pane shows the visible range).</summary>
    public IReadOnlySet<DateOnly> HighlightedDates
    {
        get => _highlighted;
        set { _highlighted = value ?? new HashSet<DateOnly>(); Invalidate(); }
    }

    /// <summary>The event colour dots of a day; null draws no dots.</summary>
    public Func<DateOnly, IReadOnlyList<Color>>? DotsProvider
    {
        get => _dots;
        set { _dots = value; Invalidate(); }
    }

    /// <summary>The tablet pane's smaller cells without dots.</summary>
    public bool IsCompact
    {
        get => _isCompact;
        set { _isCompact = value; RequestLayout(); Invalidate(); }
    }

    public System.Globalization.CultureInfo Culture
    {
        get => _culture;
        set { _culture = value ?? System.Globalization.CultureInfo.CurrentCulture; Invalidate(); UpdateDescription(); }
    }

    /// <summary>Redraws the dots after the events changed.</summary>
    public void RefreshDots() => Invalidate();

    private DateOnly[] Dates()
    {
        var dates = WinoMiniCalendarGrid.GridDates(_displayMonth, _firstDayOfWeek);
        int rows = WinoMiniCalendarGrid.Rows;
        // A last week made only of next-month days is left out (canvas CalendarMonth shows five rows for October 2026).
        while (rows > 4 && dates[(rows - 1) * WinoMiniCalendarGrid.Columns].Month != _displayMonth.Month) rows--;
        return dates[..(rows * WinoMiniCalendarGrid.Columns)];
    }

    private int CellHeight => WinoTokens.Px(Context!, _isCompact ? Resource.Dimension.wino_calendar_pane_cell_height : Resource.Dimension.wino_calendar_month_cell_height);
    private int HeaderHeight => WinoTokens.Px(Context!, _isCompact ? Resource.Dimension.wino_calendar_pane_cell_height : Resource.Dimension.wino_calendar_month_weekday_height);

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int rows = Dates().Length / WinoMiniCalendarGrid.Columns;
        int height = PaddingTop + PaddingBottom + HeaderHeight + rows * CellHeight;
        SetMeasuredDimension(MeasureSpec.GetSize(widthMeasureSpec), ResolveSize(height, heightMeasureSpec));
    }

    private float ColumnWidth => (Width - PaddingLeft - PaddingRight) / (float)WinoMiniCalendarGrid.Columns;

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (Context is not { } context) return;

        float column = ColumnWidth;
        int cellHeight = CellHeight, headerHeight = HeaderHeight;
        var secondary = WinoColors.SecondaryText(context);

        // Weekday initials.
        _textPaint.TextSize = WinoTokens.PxF(context, Resource.Dimension.wino_calendar_day_name_text);
        _textPaint.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
        _textPaint.Color = secondary;
        var names = _culture.DateTimeFormat.ShortestDayNames;
        for (int col = 0; col < WinoMiniCalendarGrid.Columns; col++)
        {
            float x = PaddingLeft + column * col + column / 2;
            DrawCentered(canvas, names[WinoMiniCalendarGrid.DayNameIndex(col, _firstDayOfWeek)], x, PaddingTop + headerHeight / 2f);
        }

        var dates = Dates();
        float radius = WinoTokens.PxF(context, _isCompact ? Resource.Dimension.wino_calendar_pane_day_circle : Resource.Dimension.wino_calendar_day_circle) / 2;
        float cellTop = WinoTokens.PxF(context, Resource.Dimension.wino_calendar_month_cell_padding_top);
        float dot = WinoTokens.PxF(context, Resource.Dimension.wino_size_dot);
        float dotGap = WinoTokens.PxF(context, Resource.Dimension.wino_calendar_month_dot_gap);
        _textPaint.TextSize = WinoTokens.PxF(context, Resource.Dimension.wino_calendar_day_number_text);

        for (int index = 0; index < dates.Length; index++)
        {
            var date = dates[index];
            int row = index / WinoMiniCalendarGrid.Columns, col = index % WinoMiniCalendarGrid.Columns;
            float left = PaddingLeft + column * col;
            float top = PaddingTop + headerHeight + row * cellHeight;
            float centerX = left + column / 2;
            float centerY = _isCompact ? top + cellHeight / 2f : top + cellTop + radius;

            if (_highlighted.Contains(date))
            {
                var (roundLeading, roundTrailing) = WinoMiniCalendarGrid.SelectionEnds(_highlighted, date, col);
                _fillPaint.Color = WinoTokens.Color(context, Resource.Color.wino_calendar_range_band);
                var band = new RectF(roundLeading ? centerX - radius : left, centerY - radius, roundTrailing ? centerX + radius : left + column, centerY + radius);
                canvas.DrawRoundRect(band, roundLeading || roundTrailing ? radius : 0, roundLeading || roundTrailing ? radius : 0, _fillPaint);
                if (!roundLeading && roundTrailing) canvas.DrawRect(left, band.Top, centerX, band.Bottom, _fillPaint);
                if (roundLeading && !roundTrailing) canvas.DrawRect(centerX, band.Top, left + column, band.Bottom, _fillPaint);
            }

            bool today = date == _today;
            bool selected = _selected == date && !today;
            if (today || selected)
            {
                _fillPaint.Color = today ? WinoColors.Accent(context) : WinoColors.SecondaryContainer(context);
                canvas.DrawCircle(centerX, centerY, radius, _fillPaint);
            }

            bool inMonth = date.Month == _displayMonth.Month;
            _textPaint.SetTypeface(Typeface.Create(today || selected ? "sans-serif-medium" : "sans-serif", TypefaceStyle.Normal));
            _textPaint.Color = today ? WinoColors.OnAccent(context) : inMonth ? WinoColors.PrimaryText(context) : WinoColors.TertiaryText(context);
            DrawCentered(canvas, date.Day.ToString(_culture), centerX, centerY);

            if (_isCompact || _dots?.Invoke(date) is not { Count: > 0 } colors) continue;
            float width = colors.Count * dot + (colors.Count - 1) * dotGap;
            float x = centerX - width / 2 + dot / 2;
            float y = centerY + radius + dotGap + dot / 2;
            foreach (var color in colors)
            {
                _fillPaint.Color = color;
                canvas.DrawCircle(x, y, dot / 2, _fillPaint);
                x += dot + dotGap;
            }
        }
    }

    private void DrawCentered(Canvas canvas, string text, float x, float centerY)
    {
        var metrics = _textPaint.GetFontMetrics();
        canvas.DrawText(text, x, centerY - ((metrics?.Ascent ?? 0) + (metrics?.Descent ?? 0)) / 2, _textPaint);
    }

    private void UpdateDescription()
        => ContentDescription = _displayMonth.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", _culture);

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is not null && _gestures?.OnTouchEvent(e) == true) return true;
        return base.OnTouchEvent(e);
    }

    private void Tapped(float x, float y)
    {
        float column = ColumnWidth;
        float gridY = y - PaddingTop - HeaderHeight;
        if (column <= 0 || gridY < 0) return;
        var dates = Dates();
        int row = (int)(gridY / CellHeight);
        int col = Math.Clamp((int)((x - PaddingLeft) / column), 0, WinoMiniCalendarGrid.Columns - 1);
        int index = row * WinoMiniCalendarGrid.Columns + col;
        if (index < 0 || index >= dates.Length) return;
        PlaySoundEffect(SoundEffects.Click);
        DateClicked?.Invoke(this, dates[index]);
    }

    private void Flung(float deltaX)
    {
        if (Context is not { } context || Math.Abs(deltaX) < WinoTokens.PxF(context, Resource.Dimension.wino_calendar_swipe_min_distance)) return;
        bool rtl = LayoutDirection == global::Android.Views.LayoutDirection.Rtl;
        if (deltaX < 0 ^ rtl) SwipedNext?.Invoke(this, EventArgs.Empty);
        else SwipedPrevious?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Gestures(WinoCalendarMonthGridView owner) : GestureDetector.SimpleOnGestureListener
    {
        public override bool OnDown(MotionEvent e) => true;

        public override bool OnSingleTapUp(MotionEvent e)
        {
            owner.Tapped(e.GetX(), e.GetY());
            return true;
        }

        public override bool OnFling(MotionEvent? e1, MotionEvent e2, float velocityX, float velocityY)
        {
            if (e1 is null || Math.Abs(velocityX) < Math.Abs(velocityY)) return false;
            owner.Flung(e2.GetX() - e1.GetX());
            return true;
        }
    }
}
