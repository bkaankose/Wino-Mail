using System.Diagnostics;
using AppKit;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Mail.Controls.AppKit.Calendar;

/// <summary>
/// Rail motion (the Windows CalendarPeriodControl.Motion and .Input): the display-linked glide,
/// settling on a period, trackpad and wheel panning, click-drag panning, the keyboard, and the
/// reaction to a visible range the view model publishes. Snap and release targets come from the
/// shared <see cref="CalendarRailMotion"/>.
/// </summary>
public sealed partial class WinoCalendarSurfaceView
{
    private const double GlideDurationMilliseconds = 320;
    private const double WheelPeriodFractionPerNotch = 0.3;
    private const double MaxWheelNotchesPerEvent = 3;
    private const double ExternalGlideMaxTravelPeriods = 2.5;
    private const double PositionEpsilon = 0.000001;

    /// <summary>Each frame closes this share of the remaining distance to the wheel target per 70 ms.</summary>
    private const double WheelSmoothingMilliseconds = 70;
    private const double WheelArrivalEpsilon = 0.0005;
    private const double WheelSettleSeconds = 0.14;

    /// <summary>A press becomes a pan after this much movement along the rail axis, more than across it.</summary>
    private const double PanStartThreshold = 6;
    private const double FlickVelocityThresholdPointsPerMillisecond = 0.35;
    private const double PanVelocitySmoothing = 0.6;
    private const double ReleaseVelocityTimeoutMilliseconds = 100;
    private const double MinReleaseGlideMilliseconds = 180;
    private const double MaxReleaseGlideMilliseconds = 520;

    /// <summary>A trackpad gesture picks the rail or the hour grid's vertical scroll after this much travel.</summary>
    private const double GestureAxisLockDistance = 4;

    private const ushort LeftArrowKeyCode = 123;
    private const ushort RightArrowKeyCode = 124;
    private const ushort DownArrowKeyCode = 125;
    private const ushort UpArrowKeyCode = 126;
    private const ushort HomeKeyCode = 115;

    private readonly Stopwatch _glideClock = new();
    private readonly Stopwatch _wheelClock = new();

    /// <summary>
    /// Units announced through <see cref="PeriodSettled"/> whose visible range has not arrived yet.
    /// A range for one of them that is not the latest is a stale answer and must not pull the rail back.
    /// </summary>
    private readonly HashSet<int> _requestedUnits = new();
    private readonly List<NSEvent> _gestureBuffer = new();

    private int _latestRequestedUnit;
    private int _announcedViewportUnit = int.MinValue;
    private CalendarRailSpace? _announcedViewportSpace;
    private bool _isGliding;
    private double _glideFrom;
    private int _glideTarget;
    private double _glideDurationMilliseconds;
    private int _lastWheelDirection;
    private bool _isWheelScrolling;
    private double _wheelTargetPosition;
    private NSTimer? _wheelSettleTimer;

    private bool _isPanning;
    private double _panStartPosition;
    private int _panStartUnit;
    private double _panLastAlong;
    private double _panLastTimestamp;
    private double _panVelocity;

    private GestureAxis _gestureAxis;
    private double _gestureX;
    private double _gestureY;

    private CADisplayLink? _displayLink;
    private FrameTarget? _frameTarget;
    private bool _isFrameDriverRunning;

    /// <summary>
    /// What the current trackpad gesture drives: undecided until it travels a few points, then the
    /// rail or the scroll view (Pass). RailEnded swallows the momentum of a gesture the rail took.
    /// </summary>
    private enum GestureAxis { None, Undecided, Rail, RailEnded, Pass }

    /// <summary>Raised when the rail comes to rest on a period other than the one <see cref="VisibleRange"/> describes.</summary>
    public event EventHandler<CalendarPeriodEventArgs>? PeriodSettled;

    /// <summary>Raised while the rail moves, whenever the period nearest to the viewport changes, so the title can follow the rail.</summary>
    public event EventHandler<CalendarPeriodEventArgs>? ViewportPeriodChanged;

    /// <summary>Raised when the user asks for today with the keyboard (Home).</summary>
    public event EventHandler? TodayRequested;

    /// <summary>
    /// Glides to the previous (<paramref name="direction"/> &lt; 0) or next period. Repeated calls
    /// during a glide add up. False when the surface shows no period yet.
    /// </summary>
    public bool NavigatePeriods(int direction)
    {
        if (direction == 0 || _space is not { } space) return false;
        int baseUnit = _isGliding
            ? _glideTarget
            : CalendarRailMotion.ChooseSnapUnit(space, _isWheelScrolling ? _wheelTargetPosition : _position, 0);
        CancelPan();
        StopWheelScrolling();
        GlideTo(space.MovePeriod(baseUnit, direction), GlideDurationMilliseconds);
        return true;
    }

    /// <summary>
    /// Reacts to the unit of a visible range the view model published. A confirmation of the period
    /// the rail settled on moves nothing; an external change (today, date picker) glides to it.
    /// </summary>
    private void AcceptVisibleRangeUnit(int targetUnit)
    {
        bool isStaleAnswer = _requestedUnits.Contains(targetUnit) && targetUnit != _latestRequestedUnit;
        if (targetUnit == _latestRequestedUnit) _requestedUnits.Clear();
        _rangeUnit = targetUnit;
        if (isStaleAnswer || targetUnit == _settledUnit || (_isGliding && _glideTarget == targetUnit)) return;
        GlideToExternalUnit(targetUnit);
    }

    private void GlideToExternalUnit(int targetUnit)
    {
        if (_space is not { } space) return;
        CancelPan();
        StopWheelScrolling();
        double maxTravel = ExternalGlideMaxTravelPeriods * space.VisibleUnitCount;
        double glideStart = CalendarRailMotion.ClampGlideStart(_position, targetUnit, maxTravel);
        if (Math.Abs(glideStart - _position) > PositionEpsilon)
        {
            StopGlide();
            SetRailPosition(glideStart);
        }
        GlideTo(targetUnit, GlideDurationMilliseconds);
    }

    /// <summary>Eases the rail to a snap unit, or jumps there when Reduce motion is on or the view is off screen.</summary>
    private void GlideTo(int targetUnit, double durationMilliseconds)
    {
        bool canAnimate = !NSWorkspace.SharedWorkspace.AccessibilityDisplayShouldReduceMotion &&
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

    /// <summary>One display frame: advance the glide or the wheel smoothing.</summary>
    private void RailFrame()
    {
        if (_space is null)
        {
            StopGlide();
            StopWheelScrolling();
            return;
        }
        if (_isPanning) return;
        if (_isWheelScrolling)
        {
            RenderWheelFrame();
            return;
        }
        if (!_isGliding)
        {
            StopFrameDriverWhenIdle();
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

    /// <summary>
    /// Moves the rail towards the accumulated wheel target by a share of the remaining distance
    /// that depends on the frame time, so wheel notches run into one continuous movement.
    /// </summary>
    private void RenderWheelFrame()
    {
        double elapsed = _wheelClock.Elapsed.TotalMilliseconds;
        double remaining = _wheelTargetPosition - _position;
        _wheelClock.Restart();
        if (Math.Abs(remaining) <= WheelArrivalEpsilon)
        {
            SetRailPosition(_wheelTargetPosition);
            _isWheelScrolling = false;
            StopFrameDriverWhenIdle();
            return;
        }
        double share = 1 - Math.Exp(-elapsed / WheelSmoothingMilliseconds);
        SetRailPosition(_position + (remaining * share));
    }

    private void StopGlide()
    {
        _isGliding = false;
        _glideClock.Stop();
        StopFrameDriverWhenIdle();
    }

    /// <summary>Drops the wheel target and its settle timer; the rail stays where it is.</summary>
    private void StopWheelScrolling()
    {
        _wheelSettleTimer?.Invalidate();
        _wheelSettleTimer = null;
        _isWheelScrolling = false;
        _wheelClock.Stop();
        StopFrameDriverWhenIdle();
    }

    /// <summary>Stops every movement without settling (the rail is rebuilt or leaves the window).</summary>
    private void StopAllMotion()
    {
        CancelPan();
        _gestureAxis = GestureAxis.None;
        _gestureBuffer.Clear();
        StopWheelScrolling();
        StopGlide();
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
    /// The rail rests on a snap unit. When that is not the period the visible range describes, or
    /// an earlier request is still unanswered, the page is asked to load it.
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

    /// <summary>
    /// A display link (macOS 14 <c>NSView.displayLink</c>) runs only while the rail glides or the
    /// wheel smooths, in the common run loop modes so it keeps ticking during event tracking.
    /// </summary>
    private void StartFrameDriver()
    {
        if (_isFrameDriverRunning) return;
        if (_displayLink is null)
        {
            _frameTarget ??= new FrameTarget(this);
            _displayLink = GetDisplayLink(_frameTarget, new Selector(FrameTarget.FrameSelector));
            _displayLink.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
        }
        _displayLink.Paused = false;
        _isFrameDriverRunning = true;
    }

    private void StopFrameDriverWhenIdle()
    {
        if (!_isFrameDriverRunning || _isGliding || _isWheelScrolling) return;
        if (_displayLink is not null) _displayLink.Paused = true;
        _isFrameDriverRunning = false;
    }

    private void ReleaseFrameDriver()
    {
        _displayLink?.Invalidate();
        _displayLink?.Dispose();
        _displayLink = null;
        _isFrameDriverRunning = false;
    }

    [Register("WinoCalendarRailFrameTarget")]
    private sealed class FrameTarget(WinoCalendarSurfaceView owner) : NSObject
    {
        public const string FrameSelector = "railFrame:";
        private readonly WeakReference<WinoCalendarSurfaceView> _owner = new(owner);

        [Export(FrameSelector)]
        public void Frame(CADisplayLink link)
        {
            if (_owner.TryGetTarget(out var surface)) surface.RailFrame();
            else link.Paused = true;
        }
    }

    // ---------------------------------------------------------------- wheel and trackpad

    public override void ScrollWheel(NSEvent theEvent)
    {
        // Events from the hour grid reach the rail through its scroll view first.
        bool fromHourGrid = !IsMonth && !_scroll.Hidden && _scroll.Frame.Contains(ConvertPointFromView(theEvent.LocationInWindow, null));
        if (!fromHourGrid && HandleRailScroll(theEvent, passed => base.ScrollWheel(passed))) return;
        base.ScrollWheel(theEvent);
    }

    /// <summary>
    /// Pans the rail with the trackpad or the wheel along its axis. The month view takes vertical
    /// scrolling; the timed views take horizontal scrolling (or Shift with a vertical wheel) and
    /// leave vertical scrolling to the hour grid. A trackpad gesture follows the fingers and, on
    /// lift, glides with its release velocity to the snap unit <see cref="CalendarRailMotion.ChooseReleaseUnit"/>
    /// picks; its system momentum is swallowed. Events a gesture held back before it chose its axis are
    /// handed to <paramref name="pass"/> when it turns out to be a vertical scroll. True when handled.
    /// </summary>
    internal bool HandleRailScroll(NSEvent theEvent, Action<NSEvent> pass)
    {
        if (_space is not { } space || _unitSize <= 0) return false;
        var phase = theEvent.Phase;
        var momentum = theEvent.MomentumPhase;

        if (momentum != NSEventPhase.None) return _gestureAxis is GestureAxis.Rail or GestureAxis.RailEnded;
        if (phase == NSEventPhase.None)
        {
            _gestureAxis = GestureAxis.None;
            return HandleWheel(space, theEvent);
        }
        if (phase.HasFlag(NSEventPhase.MayBegin))
        {
            // Fingers resting on the trackpad; the scroll view keeps its own handling of them.
            if (_gestureAxis != GestureAxis.Rail) _gestureAxis = GestureAxis.None;
            return false;
        }

        if (phase.HasFlag(NSEventPhase.Began))
        {
            _gestureBuffer.Clear();
            _gestureX = _gestureY = 0;
            _gestureAxis = _isPanning || _drag is not null || _slotPress is { Dragging: true } ? GestureAxis.Pass : GestureAxis.Undecided;
        }

        bool ends = phase.HasFlag(NSEventPhase.Ended) || phase.HasFlag(NSEventPhase.Cancelled);
        bool horizontalRail = space.Axis == CalendarRailAxis.Horizontal;
        double deltaX = theEvent.ScrollingDeltaX, deltaY = theEvent.ScrollingDeltaY;

        switch (_gestureAxis)
        {
            case GestureAxis.Undecided:
            {
                _gestureX += deltaX;
                _gestureY += deltaY;
                double along = Math.Abs(horizontalRail ? _gestureX : _gestureY);
                double across = Math.Abs(horizontalRail ? _gestureY : _gestureX);
                if (!ends && Math.Max(along, across) < GestureAxisLockDistance)
                {
                    _gestureBuffer.Add(theEvent);
                    return true;
                }
                if (!ends && along > across)
                {
                    _gestureAxis = GestureAxis.Rail;
                    _gestureBuffer.Clear();
                    BeginGesturePan(space, theEvent.Timestamp);
                    UpdateGesturePan(horizontalRail ? _gestureX : _gestureY, theEvent.Timestamp);
                    return true;
                }
                _gestureAxis = GestureAxis.Pass;
                foreach (var held in _gestureBuffer) pass(held);
                _gestureBuffer.Clear();
                return false;
            }
            case GestureAxis.Rail:
                if (ends)
                {
                    _gestureAxis = GestureAxis.RailEnded;
                    EndGesturePan(space, theEvent.Timestamp);
                    return true;
                }
                UpdateGesturePan(horizontalRail ? deltaX : deltaY, theEvent.Timestamp);
                return true;
            default:
                return false;
        }
    }

    private void BeginGesturePan(CalendarRailSpace space, double timestamp)
    {
        CancelSlotSelection();
        StopGlide();
        StopWheelScrolling();
        _panStartUnit = CalendarRailMotion.ChooseSnapUnit(space, _position, 0);
        _panLastTimestamp = timestamp * 1000;
        _panVelocity = 0;
    }

    /// <summary>The content follows the fingers: a positive delta moves it right (or down), towards earlier units.</summary>
    private void UpdateGesturePan(double delta, double timestamp)
    {
        double now = timestamp * 1000;
        if (now > _panLastTimestamp)
        {
            double sample = delta / (now - _panLastTimestamp);
            _panVelocity = (_panVelocity * (1 - PanVelocitySmoothing)) + (sample * PanVelocitySmoothing);
            _panLastTimestamp = now;
        }
        SetRailPosition(_position - (delta / _unitSize));
    }

    private void EndGesturePan(CalendarRailSpace space, double timestamp)
    {
        double idle = (timestamp * 1000) - _panLastTimestamp;
        double velocity = idle > ReleaseVelocityTimeoutMilliseconds ? 0 : _panVelocity;
        Release(space, velocity);
    }

    /// <summary>
    /// A wheel notch moves 30% of a period; notches accumulate into a target the rail eases to,
    /// and once the wheel is quiet for 140 ms the rail glides to the snap unit nearest the target,
    /// biased in the wheel's direction. Precise scrolling without gesture phases moves the rail
    /// directly and settles the same way.
    /// </summary>
    private bool HandleWheel(CalendarRailSpace space, NSEvent theEvent)
    {
        if (_isPanning || _drag is not null || _slotPress is { Dragging: true }) return false;
        double deltaX = theEvent.ScrollingDeltaX, deltaY = theEvent.ScrollingDeltaY;
        bool shift = theEvent.ModifierFlags.HasFlag(NSEventModifierMask.ShiftKeyMask);
        double delta;
        if (space.Axis == CalendarRailAxis.Vertical)
        {
            if (deltaY == 0) return false;
            delta = deltaY;
        }
        else if (deltaX != 0) delta = deltaX;
        else if (shift && deltaY != 0) delta = deltaY;
        else return false;

        if (theEvent.HasPreciseScrollingDeltas)
        {
            StopGlide();
            _isWheelScrolling = false;
            _lastWheelDirection = -Math.Sign(delta);
            SetRailPosition(_position - (delta / _unitSize));
        }
        else
        {
            double unitDelta = -Math.Clamp(delta, -MaxWheelNotchesPerEvent, MaxWheelNotchesPerEvent) * WheelPeriodFractionPerNotch * space.VisibleUnitCount;
            if (_isGliding) StopGlide();
            if (!_isWheelScrolling)
            {
                _wheelTargetPosition = _position;
                _isWheelScrolling = true;
                _wheelClock.Restart();
            }
            _wheelTargetPosition += unitDelta;
            _lastWheelDirection = Math.Sign(unitDelta);
            StartFrameDriver();
        }

        _wheelSettleTimer?.Invalidate();
        _wheelSettleTimer = NSTimer.CreateScheduledTimer(WheelSettleSeconds, false, _ => WheelSettled());
        return true;
    }

    private void WheelSettled()
    {
        _wheelSettleTimer = null;
        if (_space is not { } space || _isPanning || _isGliding) return;
        int target = CalendarRailMotion.ChooseSnapUnit(space, _isWheelScrolling ? _wheelTargetPosition : _position, _lastWheelDirection);
        _isWheelScrolling = false;
        _wheelClock.Stop();
        GlideTo(target, GlideDurationMilliseconds);
    }

    // ---------------------------------------------------------------- click-drag pan

    /// <summary>(along, across) pointer travel in window points; along is positive to the right or downwards.</summary>
    private (double Along, double Across) AxisDeltas(CGPoint downInWindow, CGPoint currentInWindow)
    {
        double deltaX = currentInWindow.X - downInWindow.X;
        double deltaY = downInWindow.Y - currentInWindow.Y; // window coordinates grow upwards
        return _space?.Axis == CalendarRailAxis.Vertical ? (deltaY, deltaX) : (deltaX, deltaY);
    }

    private void BeginPan(double along, double timestamp)
    {
        if (_space is not { } space) return;
        StopGlide();
        StopWheelScrolling();
        _isPanning = true;
        _panStartPosition = _position;
        _panStartUnit = CalendarRailMotion.ChooseSnapUnit(space, _position, 0);
        _panLastAlong = along;
        _panLastTimestamp = timestamp * 1000;
        _panVelocity = 0;
        NSCursor.ClosedHandCursor.Push();
        UpdatePan(along, timestamp);
    }

    private void UpdatePan(double along, double timestamp)
    {
        if (!_isPanning || _unitSize <= 0) return;
        double now = timestamp * 1000;
        if (now > _panLastTimestamp)
        {
            double sample = (along - _panLastAlong) / (now - _panLastTimestamp);
            _panVelocity = (_panVelocity * (1 - PanVelocitySmoothing)) + (sample * PanVelocitySmoothing);
            _panLastTimestamp = now;
        }
        _panLastAlong = along;
        SetRailPosition(_panStartPosition - (along / _unitSize));
    }

    private void EndPan(double along, double timestamp)
    {
        if (!_isPanning || _space is not { } space) { CancelPan(); return; }
        double idle = (timestamp * 1000) - _panLastTimestamp;
        UpdatePan(along, timestamp);
        // A pointer that rested before release carries no flick.
        double velocity = idle > ReleaseVelocityTimeoutMilliseconds ? 0 : _panVelocity;
        CancelPan();
        Release(space, velocity);
    }

    /// <summary>Ends a pan without gliding.</summary>
    private void CancelPan()
    {
        if (!_isPanning) return;
        _isPanning = false;
        _panVelocity = 0;
        NSCursor.ClosedHandCursor.Pop();
    }

    /// <summary>Glides from a released pan or gesture; dragging towards the start of the rail moves to later units.</summary>
    private void Release(CalendarRailSpace space, double velocityPointsPerMillisecond)
    {
        int target = CalendarRailMotion.ChooseReleaseUnit(space, _position, _panStartUnit,
            -velocityPointsPerMillisecond / _unitSize, FlickVelocityThresholdPointsPerMillisecond / _unitSize);
        GlideTo(target, GetReleaseGlideDuration(target, velocityPointsPerMillisecond));
    }

    /// <summary>
    /// A glide whose ease-out starts at the release speed: a cubic ease-out starts at three times
    /// its average speed. Slow releases use the standard duration; both are clamped.
    /// </summary>
    private double GetReleaseGlideDuration(int targetUnit, double velocityPointsPerMillisecond)
    {
        double distance = Math.Abs(targetUnit - _position) * _unitSize;
        double speed = Math.Abs(velocityPointsPerMillisecond);
        double duration = speed > 0.05 ? 3 * distance / speed : GlideDurationMilliseconds;
        return Math.Clamp(duration, MinReleaseGlideMilliseconds, MaxReleaseGlideMilliseconds);
    }

    // ---------------------------------------------------------------- keyboard

    public override bool AcceptsFirstResponder() => true;

    /// <summary>Left/Right (Up/Down in the month view) move by whole periods; Home asks for today.</summary>
    public override void KeyDown(NSEvent theEvent)
    {
        const NSEventModifierMask Commands = NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ControlKeyMask | NSEventModifierMask.AlternateKeyMask;
        if (_space is { } space && (theEvent.ModifierFlags & Commands) == 0)
        {
            bool month = space.Axis == CalendarRailAxis.Vertical;
            int direction = theEvent.KeyCode switch
            {
                LeftArrowKeyCode when !month => -1,
                RightArrowKeyCode when !month => 1,
                UpArrowKeyCode when month => -1,
                DownArrowKeyCode when month => 1,
                _ => 0
            };
            if (direction != 0)
            {
                NavigatePeriods(direction);
                return;
            }
            if (theEvent.KeyCode == HomeKeyCode)
            {
                TodayRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
        base.KeyDown(theEvent);
    }

    /// <summary>A press on the surface takes the keyboard, so the arrows and Home reach the rail.</summary>
    private void FocusSurface()
    {
        if (Window is { } window && window.FirstResponder != this) window.MakeFirstResponder(this);
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
