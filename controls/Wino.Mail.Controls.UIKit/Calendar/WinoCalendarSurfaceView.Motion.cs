using System.Diagnostics;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Mail.Controls.UIKit.Calendar;

/// <summary>
/// Rail motion (the Windows CalendarPeriodControl.Motion and .Input, the AppKit WinoCalendarSurfaceView.Motion): the
/// display-linked glide, settling on a period, the finger (and trackpad) pan along the rail axis, and the reaction to a
/// visible range the view model publishes. Snap and release targets come from the shared <see cref="CalendarRailMotion"/>.
/// Reduce Motion lands on the target without the glide.
/// </summary>
public sealed partial class WinoCalendarSurfaceView
{
    private const double GlideDurationMilliseconds = 320;
    private const double ExternalGlideMaxTravelPeriods = 2.5;
    private const double PositionEpsilon = 0.000001;
    private const double FlickVelocityThresholdPointsPerMillisecond = 0.35;
    private const double MinReleaseGlideMilliseconds = 180;
    private const double MaxReleaseGlideMilliseconds = 520;

    private readonly Stopwatch _glideClock = new();

    /// <summary>
    /// Units announced through <see cref="PeriodSettled"/> whose visible range has not arrived yet. A range for one of them
    /// that is not the latest is a stale answer and must not pull the rail back.
    /// </summary>
    private readonly HashSet<int> _requestedUnits = new();
    private readonly UIPanGestureRecognizer _railPan;

    private int _latestRequestedUnit;
    private int _announcedViewportUnit = int.MinValue;
    private CalendarRailSpace? _announcedViewportSpace;
    private bool _isGliding;
    private double _glideFrom;
    private int _glideTarget;
    private double _glideDurationMilliseconds;

    private bool _isPanning;
    private double _panStartPosition;
    private int _panStartUnit;

    private CADisplayLink? _displayLink;

    /// <summary>Raised when the rail comes to rest on a period other than the one <see cref="VisibleRange"/> describes.</summary>
    public event EventHandler<CalendarPeriodEventArgs>? PeriodSettled;

    /// <summary>Raised while the rail moves, whenever the period nearest to the viewport changes, so the title can follow the rail.</summary>
    public event EventHandler<CalendarPeriodEventArgs>? ViewportPeriodChanged;

    /// <summary>
    /// Glides to the previous (<paramref name="direction"/> &lt; 0) or next period. Repeated calls during a glide add up.
    /// False when the surface shows no period yet.
    /// </summary>
    public bool NavigatePeriods(int direction)
    {
        if (direction == 0 || _space is not { } space) return false;
        int baseUnit = _isGliding ? _glideTarget : CalendarRailMotion.ChooseSnapUnit(space, _position, 0);
        GlideTo(space.MovePeriod(baseUnit, direction), GlideDurationMilliseconds);
        return true;
    }

    /// <summary>
    /// Reacts to the unit of a visible range the view model published. A confirmation of the period the rail settled on
    /// moves nothing; an external change (today, the date panel) glides to it.
    /// </summary>
    private void AcceptVisibleRangeUnit(int targetUnit)
    {
        bool isStaleAnswer = _requestedUnits.Contains(targetUnit) && targetUnit != _latestRequestedUnit;
        if (targetUnit == _latestRequestedUnit) _requestedUnits.Clear();
        _rangeUnit = targetUnit;
        if (isStaleAnswer || targetUnit == _settledUnit || (_isGliding && _glideTarget == targetUnit)) return;
        // The finger owns the rail; the release settles and asks again.
        if (_isPanning || _space is not { } space) return;

        // A long jump lands close first, then glides, instead of flying through every period between.
        double maxTravel = ExternalGlideMaxTravelPeriods * space.VisibleUnitCount;
        double glideStart = CalendarRailMotion.ClampGlideStart(_position, targetUnit, maxTravel);
        if (Math.Abs(glideStart - _position) > PositionEpsilon)
        {
            StopGlide();
            SetRailPosition(glideStart);
        }
        GlideTo(targetUnit, GlideDurationMilliseconds);
    }

    /// <summary>Eases the rail to a snap unit, or jumps there when Reduce Motion is on or the view is off screen.</summary>
    private void GlideTo(int targetUnit, double durationMilliseconds)
    {
        bool canAnimate = !UIAccessibility.IsReduceMotionEnabled &&
                          Window is not null &&
                          durationMilliseconds > 0 &&
                          Math.Abs(targetUnit - _position) > PositionEpsilon;
        if (!canAnimate)
        {
            StopGlide();
            SetRailPosition(targetUnit);
            SettleRail(targetUnit);
            return;
        }

        _glideFrom = _position;
        _glideTarget = targetUnit;
        _glideDurationMilliseconds = durationMilliseconds;
        _isGliding = true;
        _glideClock.Restart();
        StartFrameDriver();
    }

    /// <summary>One display frame of a glide.</summary>
    private void RailFrame()
    {
        if (_space is null || !_isGliding)
        {
            StopGlide();
            return;
        }

        double progress = _glideClock.Elapsed.TotalMilliseconds / _glideDurationMilliseconds;
        if (progress >= 1)
        {
            int targetUnit = _glideTarget;
            StopGlide();
            SetRailPosition(targetUnit);
            SettleRail(targetUnit);
            return;
        }
        SetRailPosition(_glideFrom + ((_glideTarget - _glideFrom) * CalendarRailMotion.EaseOut(progress)));
    }

    private void StopGlide()
    {
        _isGliding = false;
        _glideClock.Stop();
        if (_displayLink is not null) _displayLink.Paused = true;
    }

    /// <summary>Stops every movement without settling (the rail is rebuilt or leaves the window).</summary>
    private void StopAllMotion()
    {
        _isPanning = false;
        StopGlide();
    }

    /// <summary>The surface leaves the window: a glide in flight lands at once, so the rail and the loaded period stay in step.</summary>
    private void LandRail()
    {
        bool wasGliding = _isGliding;
        int glideTarget = _glideTarget;
        StopAllMotion();
        if (wasGliding)
        {
            SetRailPosition(glideTarget);
            SettleRail(glideTarget);
        }
        // The display link belongs to the old screen; a new one is made on the next glide.
        ReleaseFrameDriver();
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
        if (_space is not { } space) return;
        int nearestUnit = CalendarRailMotion.ChooseSnapUnit(space, _position, 0);
        if (nearestUnit == _announcedViewportUnit && ReferenceEquals(space, _announcedViewportSpace)) return;
        _announcedViewportUnit = nearestUnit;
        _announcedViewportSpace = space;
        ViewportPeriodChanged?.Invoke(this, new CalendarPeriodEventArgs(space.DisplayType, space.GetAnchorDate(nearestUnit)));
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
        PeriodSettled?.Invoke(this, new CalendarPeriodEventArgs(space.DisplayType, space.GetAnchorDate(unit)));
    }

    // ---------------------------------------------------------------- frame driver

    /// <summary>A display link that runs only while the rail glides, in the common run loop modes so it keeps ticking during tracking.</summary>
    private void StartFrameDriver()
    {
        if (_displayLink is null)
        {
            // The run loop retains the link and the link its block, so the block holds the surface weakly.
            var self = new WeakReference<WinoCalendarSurfaceView>(this);
            _displayLink = CADisplayLink.Create(() =>
            {
                if (self.TryGetTarget(out var surface)) surface.RailFrame();
            });
            _displayLink.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
        }
        _displayLink.Paused = false;
    }

    private void ReleaseFrameDriver()
    {
        _displayLink?.Invalidate();
        _displayLink?.Dispose();
        _displayLink = null;
    }

    // ---------------------------------------------------------------- pan

    /// <summary>
    /// The pan along the rail axis: day columns move horizontally, month rows vertically. It starts only when the movement
    /// runs along the axis more than across it, so the hour grid keeps its vertical scroll (which waits for this pan to
    /// fail) and taps and long presses keep working. Two-finger trackpad scrolling drives it as well.
    /// </summary>
    private UIPanGestureRecognizer CreateRailPan()
    {
        var pan = new UIPanGestureRecognizer(RailPanned)
        {
            MaximumNumberOfTouches = 1,
            AllowedScrollTypesMask = UIScrollTypeMask.All
        };
        pan.ShouldBegin = recognizer =>
        {
            if (_space is not { } space || _unitSize <= 0 || _drag is not null || _slotPress is not null) return false;
            var pan = (UIPanGestureRecognizer)recognizer;
            var motion = pan.VelocityInView(this);
            // A scroll gesture can start without velocity; its travel decides then.
            if (motion.X == 0 && motion.Y == 0) motion = pan.TranslationInView(this);
            double along = Math.Abs(space.Axis == CalendarRailAxis.Horizontal ? motion.X : motion.Y);
            double across = Math.Abs(space.Axis == CalendarRailAxis.Horizontal ? motion.Y : motion.X);
            return along > across;
        };
        AddGestureRecognizer(pan);
        _scroll.PanGestureRecognizer.RequireGestureRecognizerToFail(pan);
        return pan;
    }

    /// <summary>The content follows the finger: a pan right (or down) moves towards earlier units.</summary>
    private void RailPanned(UIPanGestureRecognizer recognizer)
    {
        if (_space is not { } space) return;
        bool horizontal = space.Axis == CalendarRailAxis.Horizontal;
        var translation = recognizer.TranslationInView(this);
        double along = horizontal ? translation.X : translation.Y;
        switch (recognizer.State)
        {
            case UIGestureRecognizerState.Began:
                StopGlide();
                CancelSlotSelection();
                _isPanning = true;
                _panStartPosition = _position;
                _panStartUnit = CalendarRailMotion.ChooseSnapUnit(space, _position, 0);
                break;
            case UIGestureRecognizerState.Changed:
                if (_isPanning && _unitSize > 0) SetRailPosition(_panStartPosition - (along / _unitSize));
                break;
            case UIGestureRecognizerState.Ended:
                if (!_isPanning) break;
                _isPanning = false;
                if (_unitSize > 0) SetRailPosition(_panStartPosition - (along / _unitSize));
                var velocity = recognizer.VelocityInView(this);
                Release(space, (horizontal ? velocity.X : velocity.Y) / 1000);
                break;
            case UIGestureRecognizerState.Cancelled:
            case UIGestureRecognizerState.Failed:
                if (!_isPanning) break;
                // An interrupted pan still comes to rest on a period.
                _isPanning = false;
                GlideTo(CalendarRailMotion.ChooseSnapUnit(space, _position, 0), GlideDurationMilliseconds);
                break;
        }
    }

    /// <summary>Glides from a released pan; dragging towards the start of the rail moves to later units.</summary>
    private void Release(CalendarRailSpace space, double velocityPointsPerMillisecond)
    {
        if (_unitSize <= 0) return;
        int target = CalendarRailMotion.ChooseReleaseUnit(space, _position, _panStartUnit,
            -velocityPointsPerMillisecond / _unitSize, FlickVelocityThresholdPointsPerMillisecond / _unitSize);
        // A glide whose ease-out starts at the release speed: a cubic ease-out starts at three times its average speed.
        double distance = Math.Abs(target - _position) * _unitSize;
        double speed = Math.Abs(velocityPointsPerMillisecond);
        double duration = speed > 0.05 ? 3 * distance / speed : GlideDurationMilliseconds;
        GlideTo(target, Math.Clamp(duration, MinReleaseGlideMilliseconds, MaxReleaseGlideMilliseconds));
    }

#if DEBUG
    /// <summary>The fractional rail position, for the debug bridge.</summary>
    public double DebugRailPosition => _position;

    /// <summary>Puts the rail at <paramref name="position"/> without settling, for screenshots of a moving rail.</summary>
    public void DebugSetRailPosition(double position)
    {
        StopAllMotion();
        SetRailPosition(position);
    }

    /// <summary>Glides from wherever the rail is to the nearest snap unit, like a release without velocity.</summary>
    public void DebugSettleRail()
    {
        if (_space is not { } space) return;
        GlideTo(CalendarRailMotion.ChooseSnapUnit(space, _position, 0), GlideDurationMilliseconds);
    }
#endif
}
