using Android.Animation;
using Android.Views;
using Android.Views.Animations;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Core.Calendar;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.Calendar;

/// <summary>
/// A period on the rail (twin of the AppKit and UIKit CalendarPeriodEventArgs): raised when the rail settles on one the
/// view model has not loaded, and while it moves, whenever the period nearest to the viewport changes.
/// </summary>
public sealed class CalendarPeriodEventArgs(CalendarDisplayType displayType, DateOnly anchorDate) : EventArgs
{
    public CalendarDisplayType DisplayType { get; } = displayType;

    /// <summary>The anchor to resolve with <see cref="CalendarRangeResolver"/>; the resolved range starts on the unit the rail settled on.</summary>
    public DateOnly AnchorDate { get; } = anchorDate;
}

/// <summary>
/// The infinite rail (docs/calendar-rail-navigation.md; the Windows CalendarPeriodControl.Rail and .Motion, the AppKit
/// WinoCalendarSurfaceView.Rail and .Motion). Day, Week and Work week pan horizontally over the day columns of the shared
/// <see cref="CalendarRailSpace"/>. The month view pans horizontally over whole month pages instead of the shared vertical
/// week rows (owner decision, 22 Oct 2026): a vertical pull stays pull to refresh, the only sync gesture on Android. A month
/// page is a unit that snaps one at a time like a day column, so the shared <see cref="CalendarRailMotion"/> picks its
/// targets over a Day space. A press becomes a pan after 6 dp of dominant horizontal movement, so taps and the hour grid's
/// vertical scroll keep working; the strips draw the realized window translated by the rail offset every frame and lay
/// it out again only when the window shifts. Glides run on a <see cref="ValueAnimator"/>, so the animator duration scale
/// applies (0 jumps).
/// </summary>
public sealed partial class WinoCalendarSurfaceView
{
    /// <summary>Units realized before the first visible unit, and after the last one, when the window is re-centred.</summary>
    private const int SpareUnitCount = 3;

    /// <summary>Units that must stay realized beyond each side of the viewport before a shift is due.</summary>
    private const int WindowMarginUnits = 1;

    private const double GlideDurationMilliseconds = 320;
    private const double ExternalGlideMaxTravelPeriods = 2.5;
    private const double PositionEpsilon = 0.000001;
    private const double PanStartThresholdDp = 6;
    private const double FlickVelocityThresholdDpPerMillisecond = 0.35;
    private const double MinReleaseGlideMilliseconds = 180;
    private const double MaxReleaseGlideMilliseconds = 520;
    private const double PanVelocitySmoothing = 0.6;
    private const double ReleaseVelocityTimeoutMilliseconds = 100;

    private readonly List<DateOnly> _windowDates = [];

    /// <summary>Units announced through <see cref="PeriodSettled"/> whose visible range has not arrived yet; a stale answer must not pull the rail back.</summary>
    private readonly HashSet<int> _requestedUnits = [];

    private CalendarRailSpace? _space;
    private CalendarRailSpace? _motionSpace;
    private double _position;
    private int _settledUnit;
    private int _rangeUnit;
    private int _latestRequestedUnit;
    private int _windowStart;
    private int _windowCount;
    private int _layoutVersion;
    private int _announcedViewportUnit = int.MinValue;
    private CalendarRailSpace? _announcedViewportSpace;

    private ValueAnimator? _glide;
    private int _glideTarget;

    private bool _tracking;
    private bool _axisDecided;
    private bool _isPanning;
    private float _downX;
    private float _downY;
    private double _panStartPosition;
    private int _panStartUnit;
    private float _panLastX;
    private long _panLastTime;
    private double _panVelocity;

    /// <summary>Raised when the rail comes to rest on a period other than the one <see cref="VisibleRange"/> describes.</summary>
    public event EventHandler<CalendarPeriodEventArgs>? PeriodSettled;

    /// <summary>Raised while the rail moves, whenever the period nearest to the viewport changes, so the title can follow the rail.</summary>
    public event EventHandler<CalendarPeriodEventArgs>? ViewportPeriodChanged;

    /// <summary>The month view pages over whole months; the other views rail over day columns.</summary>
    private bool IsMonthPages => _space?.DisplayType == CalendarDisplayType.Month;

    /// <summary>Columns (or month pages) the viewport shows at once.</summary>
    private int VisibleUnitCount => IsMonthPages ? 1 : _space?.VisibleUnitCount ?? 1;

    /// <summary>The width of a day column, or of a month page, in pixels.</summary>
    private float UnitPx => IsMonthPages ? Width : Math.Max(0, Width - GutterPx) / VisibleUnitCount;

    /// <summary>How far the realized window's first unit sits left of the viewport, in pixels.</summary>
    private float RailOffsetPx => (float)((_position - _windowStart) * UnitPx);

    /// <summary>The left edge of a realized column in surface coordinates.</summary>
    private float ColumnLeft(int index) => GutterPx + index * UnitPx - RailOffsetPx;

    /// <summary>The realized column under <paramref name="x"/> (surface coordinates), or -1 in the gutter or outside the window.</summary>
    private int ColumnAt(float x)
    {
        float unit = UnitPx;
        if (unit <= 0 || x < GutterPx) return -1;
        int index = (int)Math.Floor((x - GutterPx + RailOffsetPx) / unit);
        return index >= 0 && index < _windowDates.Count ? index : -1;
    }

    private static int MonthIndex(DateOnly date) => date.Year * 12 + date.Month - 1;

    private static DateOnly MonthStart(int monthIndex) => new(Math.DivRem(monthIndex, 12, out int month), month + 1, 1);

    private DateOnly AnchorDate(int unit) => IsMonthPages ? MonthStart(unit) : _space?.GetAnchorDate(unit) ?? default;

    /// <summary>
    /// Builds the rail space for the published range and settings. A new display type or week layout rebuilds the rail on
    /// the period; otherwise the rail accepts the period (a glide when it is not the one it rests on).
    /// </summary>
    private void ApplyRange()
    {
        if (_range is not { } range || _settings is not { } settings)
        {
            StopAllMotion();
            _space = _motionSpace = null;
            _windowDates.Clear();
            _windowCount = 0;
            InvalidateRail();
            return;
        }

        var space = CalendarRailSpace.Create(range.DisplayType, settings);
        bool isRebuild = _space is null || !_space.HasSameUnits(space);
        _space = space;
        _motionSpace = space.DisplayType == CalendarDisplayType.Month ? CalendarRailSpace.Create(CalendarDisplayType.Day, settings) : space;
        int targetUnit = IsMonthPages ? MonthIndex(range.AnchorDate) : space.GetPeriodStartUnit(range.AnchorDate);

        if (isRebuild)
        {
            StopAllMotion();
            _requestedUnits.Clear();
            _position = targetUnit;
            _settledUnit = targetUnit;
            _rangeUnit = targetUnit;
            _announcedViewportUnit = int.MinValue;
            InvalidateRail();
            return;
        }

        InvalidateRail();
        AcceptVisibleRangeUnit(targetUnit);
    }

    /// <summary>The window and every strip's layout must be rebuilt (range, settings, items or size changed).</summary>
    private void InvalidateRail()
    {
        _layoutVersion++;
        UpdateRailWindow(force: true);
    }

    /// <summary>
    /// Brings the realized window in line with the position. Inside the window only the offset changes and the strips
    /// redraw; a shift (or <paramref name="force"/>) realizes the new window and lays the strips out again.
    /// </summary>
    private void UpdateRailWindow(bool force)
    {
        if (_space is not { } space || IsMonthPages)
        {
            if (_windowCount > 0)
            {
                _windowDates.Clear();
                _windowCount = 0;
                _windowStart = 0;
                _layoutVersion++;
                _allDay.RequestLayout();
            }
            InvalidateStrips();
            return;
        }

        int firstVisibleUnit = (int)Math.Floor(_position);
        int lastVisibleUnit = (int)Math.Ceiling(_position) + space.VisibleUnitCount - 1;
        int windowCount = space.VisibleUnitCount + 1 + SpareUnitCount * 2;
        bool isCovered = !force &&
                         _windowCount == windowCount &&
                         firstVisibleUnit - WindowMarginUnits >= _windowStart &&
                         lastVisibleUnit + WindowMarginUnits < _windowStart + _windowCount;
        if (!isCovered)
        {
            _windowStart = firstVisibleUnit - SpareUnitCount;
            _windowCount = windowCount;
            _windowDates.Clear();
            for (int unit = _windowStart; unit < _windowStart + _windowCount; unit++) _windowDates.Add(space.GetUnitStartDate(unit));
            _layoutVersion++;
            // The all-day strip's height follows the lanes of the realized window.
            _allDay.RequestLayout();
        }
        InvalidateStrips();
    }

    private void SetRailPosition(double position)
    {
        _position = position;
        UpdateRailWindow(force: false);
        AnnounceViewportPeriod();
    }

    /// <summary>Tells the page which period sits nearest to the viewport while the rail moves.</summary>
    private void AnnounceViewportPeriod()
    {
        if (_space is not { } space || _motionSpace is not { } motion) return;
        int nearestUnit = CalendarRailMotion.ChooseSnapUnit(motion, _position, 0);
        if (nearestUnit == _announcedViewportUnit && ReferenceEquals(space, _announcedViewportSpace)) return;
        _announcedViewportUnit = nearestUnit;
        _announcedViewportSpace = space;
        ViewportPeriodChanged?.Invoke(this, new CalendarPeriodEventArgs(space.DisplayType, AnchorDate(nearestUnit)));
    }

    /// <summary>
    /// Glides to the previous (<paramref name="direction"/> &lt; 0) or next period. Repeated calls during a glide add up.
    /// False when the surface shows no period yet.
    /// </summary>
    public bool NavigatePeriods(int direction)
    {
        if (direction == 0 || _space is not { } space || _motionSpace is not { } motion) return false;
        int baseUnit = _glide is not null ? _glideTarget : CalendarRailMotion.ChooseSnapUnit(motion, _position, 0);
        CancelPan();
        int target = IsMonthPages ? baseUnit + Math.Sign(direction) : space.MovePeriod(baseUnit, direction);
        GlideTo(target, GlideDurationMilliseconds);
        return true;
    }

    /// <summary>
    /// Reacts to the unit of a visible range the view model published. A confirmation of the period the rail settled on
    /// moves nothing; an external change (today, the date picker) glides to it.
    /// </summary>
    private void AcceptVisibleRangeUnit(int targetUnit)
    {
        bool isStaleAnswer = _requestedUnits.Contains(targetUnit) && targetUnit != _latestRequestedUnit;
        if (targetUnit == _latestRequestedUnit) _requestedUnits.Clear();
        _rangeUnit = targetUnit;
        if (isStaleAnswer || targetUnit == _settledUnit || (_glide is not null && _glideTarget == targetUnit)) return;
        if (_isPanning) return; // The finger owns the rail; the release settles and asks again.

        // A long jump lands close first, then glides, instead of flying through every period between.
        double maxTravel = ExternalGlideMaxTravelPeriods * VisibleUnitCount;
        double glideStart = CalendarRailMotion.ClampGlideStart(_position, targetUnit, maxTravel);
        if (Math.Abs(glideStart - _position) > PositionEpsilon)
        {
            StopGlide();
            SetRailPosition(glideStart);
        }
        GlideTo(targetUnit, GlideDurationMilliseconds);
    }

    /// <summary>Eases the rail to a snap unit, or jumps there when the view is detached or there is nothing to travel.</summary>
    private void GlideTo(int targetUnit, double durationMilliseconds)
    {
        StopGlide();
        if (!IsAttachedToWindow || durationMilliseconds <= 0 || Math.Abs(targetUnit - _position) <= PositionEpsilon)
        {
            SetRailPosition(targetUnit);
            SettleRail(targetUnit);
            return;
        }

        double from = _position;
        var glide = ValueAnimator.OfFloat(0f, 1f)!;
        glide.SetDuration((long)durationMilliseconds);
        glide.SetInterpolator(new LinearInterpolator());
        glide.Update += (sender, args) =>
        {
            if (!ReferenceEquals(sender, _glide)) return;
            SetRailPosition(from + (targetUnit - from) * CalendarRailMotion.EaseOut(args.Animation.AnimatedFraction));
        };
        // The animator duration scale applies: at 0 the animator ends on the next frame and the rail jumps.
        glide.AnimationEnd += (sender, _) =>
        {
            if (!ReferenceEquals(sender, _glide)) return;
            _glide = null;
            SetRailPosition(targetUnit);
            SettleRail(targetUnit);
        };
        _glide = glide;
        _glideTarget = targetUnit;
        glide.Start();
    }

    /// <summary>Stops a glide where it is, without settling.</summary>
    private void StopGlide()
    {
        var glide = _glide;
        _glide = null;
        glide?.Cancel();
    }

    private void StopAllMotion()
    {
        CancelPan();
        StopGlide();
    }

    /// <summary>
    /// The rail rests on a snap unit. When that is not the period the visible range describes, or an earlier request is
    /// still unanswered, the page is asked to load it.
    /// </summary>
    private void SettleRail(int unit)
    {
        _settledUnit = unit;
        if (_space is not { } space || (unit == _rangeUnit && _requestedUnits.Count == 0)) return;
        _requestedUnits.Add(unit);
        _latestRequestedUnit = unit;
        PeriodSettled?.Invoke(this, new CalendarPeriodEventArgs(space.DisplayType, AnchorDate(unit)));
    }

    // ---- Touch ----

    /// <summary>
    /// Watches every press on the strips. A press that travels 6 dp, more horizontally than vertically, becomes a pan and
    /// the surface takes the gesture from its children (the tile taps, the hour grid's vertical scroll) and from the pull
    /// to refresh above it. A press during a glide catches the rail.
    /// </summary>
    public override bool OnInterceptTouchEvent(MotionEvent? e) => e is not null && TrackRailTouch(e) || base.OnInterceptTouchEvent(e);

    /// <summary>Presses no child takes (the day header) arrive here directly; the same tracking applies.</summary>
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return base.OnTouchEvent(e);
        bool panning = TrackRailTouch(e);
        return panning || e.ActionMasked == MotionEventActions.Down || base.OnTouchEvent(e);
    }

    /// <summary>Feeds one touch event to the rail; true while the rail owns the gesture.</summary>
    private bool TrackRailTouch(MotionEvent e)
    {
        if (_space is null || UnitPx <= 0) return false;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _downX = e.GetX();
                _downY = e.GetY();
                _tracking = true;
                _axisDecided = false;
                if (_glide is not null)
                {
                    BeginPan(e);
                    return true;
                }
                return false;
            case MotionEventActions.Move:
                if (!_tracking) return _isPanning;
                float deltaX = e.GetX() - _downX, deltaY = e.GetY() - _downY;
                if (!_isPanning && !_axisDecided)
                {
                    float threshold = WinoUnits.DpF(Context!, PanStartThresholdDp);
                    if (Math.Abs(deltaX) >= threshold && Math.Abs(deltaX) > Math.Abs(deltaY)) BeginPan(e);
                    else if (Math.Abs(deltaY) >= threshold) _axisDecided = true;
                }
                if (_isPanning)
                {
                    TrackVelocity(e);
                    UpdatePan(deltaX);
                }
                return _isPanning;
            case MotionEventActions.Up:
                return EndTracking(release: true, e.EventTime);
            case MotionEventActions.Cancel:
                return EndTracking(release: false, e.EventTime);
            default:
                return _isPanning;
        }
    }

    private void BeginPan(MotionEvent e)
    {
        if (_motionSpace is not { } motion) return;
        StopGlide();
        _isPanning = true;
        _axisDecided = true;
        _panStartPosition = _position;
        _panStartUnit = CalendarRailMotion.ChooseSnapUnit(motion, _position, 0);
        _panLastX = e.GetX();
        _panLastTime = e.EventTime;
        _panVelocity = 0;
        Parent?.RequestDisallowInterceptTouchEvent(true);
    }

    /// <summary>
    /// The release velocity (the AppKit rail's rule): every sample, batched ones included, blends into a smoothed velocity
    /// in pixels per millisecond.
    /// </summary>
    private void TrackVelocity(MotionEvent e)
    {
        for (int index = 0; index <= e.HistorySize; index++)
        {
            bool current = index == e.HistorySize;
            float x = current ? e.GetX() : e.GetHistoricalX(index);
            long time = current ? e.EventTime : e.GetHistoricalEventTime(index);
            if (time <= _panLastTime) continue;
            double sample = (x - _panLastX) / (double)(time - _panLastTime);
            _panVelocity = (_panVelocity * (1 - PanVelocitySmoothing)) + (sample * PanVelocitySmoothing);
            _panLastX = x;
            _panLastTime = time;
        }
    }

    /// <summary>The content follows the finger from the press: dragging right moves towards earlier units.</summary>
    private void UpdatePan(float deltaX)
    {
        float unit = UnitPx;
        if (unit > 0) SetRailPosition(_panStartPosition - deltaX / unit);
    }

    private bool EndTracking(bool release, long eventTime)
    {
        bool wasPanning = _isPanning;
        _tracking = false;
        _axisDecided = false;
        if (!wasPanning) return false;
        // A finger that rested before lifting carries no flick.
        double velocity = release && eventTime - _panLastTime <= ReleaseVelocityTimeoutMilliseconds ? _panVelocity : 0;
        _isPanning = false;
        Parent?.RequestDisallowInterceptTouchEvent(false);
        Release(velocity);
        return true;
    }

    /// <summary>Ends a pan without gliding (the rail is rebuilt).</summary>
    private void CancelPan()
    {
        if (!_isPanning) return;
        _isPanning = false;
        _tracking = false;
        Parent?.RequestDisallowInterceptTouchEvent(false);
    }

    /// <summary>Glides from a released pan with its velocity (pixels per millisecond, positive to the right).</summary>
    private void Release(double velocityPxPerMillisecond)
    {
        if (_motionSpace is not { } motion || UnitPx <= 0) return;
        double unit = UnitPx;
        double flick = FlickVelocityThresholdDpPerMillisecond * Density;
        int target = CalendarRailMotion.ChooseReleaseUnit(motion, _position, _panStartUnit, -velocityPxPerMillisecond / unit, flick / unit);
        // A glide whose ease-out starts at the release speed: a cubic ease-out starts at three times its average speed.
        double distance = Math.Abs(target - _position) * unit;
        double speed = Math.Abs(velocityPxPerMillisecond);
        double duration = speed > 0.05 * Density ? 3 * distance / speed : GlideDurationMilliseconds;
        GlideTo(target, Math.Clamp(duration, MinReleaseGlideMilliseconds, MaxReleaseGlideMilliseconds));
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        if (w != oldw) InvalidateRail();
    }

    /// <summary>Today's index among the realized columns, or -1.</summary>
    private int TodayColumn() => WinoCalendarSurfaceGeometry.IndexOf(_windowDates, Today());
}
