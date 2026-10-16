using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.Calendar;

/// <summary>
/// The phone calendar's week strip under the top app bar (canvas Material pass board Calendar): one column per day with
/// the short day name over the day number. The selected day is the filled accent circle, today's number is accent
/// text, weekend numbers are secondary (date picker option B). A tap raises <see cref="DateClicked"/>; a horizontal fling raises
/// <see cref="SwipedNext"/> or <see cref="SwipedPrevious"/> (the page moves a week). Drawn on one canvas; every size and
/// colour is a calendar token. Usable from XML as <c>com.winomail.controls.WinoCalendarWeekStripView</c>.
/// </summary>
[Register("com.winomail.controls.WinoCalendarWeekStripView")]
public sealed class WinoCalendarWeekStripView : View
{
    private readonly Paint _namePaint = new(PaintFlags.AntiAlias);
    private readonly Paint _numberPaint = new(PaintFlags.AntiAlias);
    private readonly Paint _circlePaint = new(PaintFlags.AntiAlias);
    private GestureDetector? _gestures;
    private IReadOnlyList<DateOnly> _dates = [];
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly? _selected;
    private System.Globalization.CultureInfo _culture = System.Globalization.CultureInfo.CurrentCulture;

    public WinoCalendarWeekStripView(Context context) : base(context) => Initialize(context);
    public WinoCalendarWeekStripView(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize(context);
    private WinoCalendarWeekStripView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    /// <summary>A day was tapped.</summary>
    public event EventHandler<DateOnly>? DateClicked;

    /// <summary>A fling towards the start: the next week.</summary>
    public event EventHandler? SwipedNext;

    /// <summary>A fling towards the end: the previous week.</summary>
    public event EventHandler? SwipedPrevious;

    private void Initialize(Context context)
    {
        _gestures = new GestureDetector(context, new Gestures(this));
        Clickable = true;
        Focusable = true;
        _namePaint.TextAlign = Paint.Align.Center;
        _namePaint.TextSize = WinoTokens.PxF(context, Resource.Dimension.wino_calendar_day_name_text);
        _namePaint.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
        _namePaint.LetterSpacing = 0.05f;
        _numberPaint.TextAlign = Paint.Align.Center;
        _numberPaint.TextSize = WinoTokens.PxF(context, Resource.Dimension.wino_calendar_day_number_text);
        _numberPaint.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
        _circlePaint.StrokeWidth = WinoTokens.PxF(context, Resource.Dimension.wino_m3_stroke);
    }

    /// <summary>The days shown, left to right (a week).</summary>
    public IReadOnlyList<DateOnly> Dates
    {
        get => _dates;
        set { _dates = value ?? []; UpdateDescription(); Invalidate(); }
    }

    public DateOnly Today
    {
        get => _today;
        set { _today = value; Invalidate(); }
    }

    /// <summary>The day shown (drawn in the accent circle).</summary>
    public DateOnly? SelectedDate
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public System.Globalization.CultureInfo Culture
    {
        get => _culture;
        set { _culture = value ?? System.Globalization.CultureInfo.CurrentCulture; UpdateDescription(); Invalidate(); }
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int height = WinoTokens.Px(Context!, Resource.Dimension.wino_calendar_week_strip_height);
        SetMeasuredDimension(MeasureSpec.GetSize(widthMeasureSpec), ResolveSize(height, heightMeasureSpec));
    }

    private float ColumnWidth => _dates.Count == 0 ? 0 : (Width - PaddingLeft - PaddingRight) / (float)_dates.Count;

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (Context is not { } context || _dates.Count == 0) return;

        float column = ColumnWidth;
        float radius = WinoTokens.PxF(context, Resource.Dimension.wino_calendar_day_circle) / 2;
        float gap = WinoTokens.PxF(context, Resource.Dimension.wino_calendar_day_name_gap);
        var nameMetrics = _namePaint.GetFontMetrics();
        var numberMetrics = _numberPaint.GetFontMetrics();
        float nameHeight = (nameMetrics?.Descent ?? 0) - (nameMetrics?.Ascent ?? 0);
        float blockHeight = nameHeight + gap + radius * 2;
        float top = (Height - blockHeight) / 2;
        var accent = WinoColors.Accent(context);
        var secondary = WinoColors.SecondaryText(context);

        for (int index = 0; index < _dates.Count; index++)
        {
            var date = _dates[index];
            float centerX = PaddingLeft + column * index + column / 2;
            _namePaint.Color = secondary;
            canvas.DrawText(DayName(date), centerX, top - (nameMetrics?.Ascent ?? 0), _namePaint);

            float centerY = top + nameHeight + gap + radius;
            // Date picker option B (15 Oct 2026): the selected day in an accent circle, today in accent text.
            bool today = date == _today;
            bool selected = _selected == date;
            if (selected)
            {
                _circlePaint.SetStyle(Paint.Style.Fill);
                _circlePaint.Color = accent;
                canvas.DrawCircle(centerX, centerY, radius, _circlePaint);
            }

            bool weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            _numberPaint.Color = selected ? WinoColors.OnAccent(context) : today ? WinoColors.AccentText(context) : weekend ? secondary : WinoColors.PrimaryText(context);            float baseline = centerY - ((numberMetrics?.Ascent ?? 0) + (numberMetrics?.Descent ?? 0)) / 2;
            canvas.DrawText(date.Day.ToString(_culture), centerX, baseline, _numberPaint);
        }
    }

    private string DayName(DateOnly date) => _culture.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek).ToUpper(_culture);

    private void UpdateDescription()
        => ContentDescription = _dates.Count == 0 ? null : string.Join(", ", _dates.Select(date => date.ToDateTime(TimeOnly.MinValue).ToString("D", _culture)));

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is not null && _gestures?.OnTouchEvent(e) == true) return true;
        return base.OnTouchEvent(e);
    }

    private void Tapped(float x)
    {
        float column = ColumnWidth;
        if (column <= 0) return;
        int index = Math.Clamp((int)((x - PaddingLeft) / column), 0, _dates.Count - 1);
        PlaySoundEffect(SoundEffects.Click);
        DateClicked?.Invoke(this, _dates[index]);
    }

    private void Flung(float deltaX)
    {
        if (Context is not { } context || Math.Abs(deltaX) < WinoTokens.PxF(context, Resource.Dimension.wino_calendar_swipe_min_distance)) return;
        bool rtl = LayoutDirection == global::Android.Views.LayoutDirection.Rtl;
        if (deltaX < 0 ^ rtl) SwipedNext?.Invoke(this, EventArgs.Empty);
        else SwipedPrevious?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Gestures(WinoCalendarWeekStripView owner) : GestureDetector.SimpleOnGestureListener
    {
        public override bool OnDown(MotionEvent e) => true;

        public override bool OnSingleTapUp(MotionEvent e)
        {
            owner.Tapped(e.GetX());
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
