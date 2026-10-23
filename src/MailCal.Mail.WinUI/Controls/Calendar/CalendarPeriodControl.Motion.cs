using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using MailCal.Core.Domain.Models.Calendar;

namespace MailCal.Calendar.Controls;

/// <summary>
/// Rail motion: the frame-driven glide, settling on a period, wheel panning and the reaction to
/// a visible range the view model publishes.
/// </summary>
public sealed partial class CalendarPeriodControl
{
    private const double GlideDurationMilliseconds = 320d;
    private const double WheelNotchDelta = 120d;
    private const double WheelPeriodFractionPerNotch = 0.3d;
    private const double ExternalGlideMaxTravelPeriods = 2.5d;
    private const double PositionEpsilon = 0.000001d;

    /// <summary>
    /// Time constant of the wheel smoothing: each frame closes this share of the remaining distance
    /// to the accumulated wheel target per 70 ms, so notches run into one continuous movement.
    /// </summary>
    private const double WheelSmoothingMilliseconds = 70d;
    private const double WheelArrivalEpsilon = 0.0005d;
    private static readonly TimeSpan WheelSettleDelay = TimeSpan.FromMilliseconds(140);

    private readonly UISettings _uiSettings = new();
    private readonly Stopwatch _glideClock = new();
    private readonly Stopwatch _wheelClock = new();

    /// <summary>
    /// Units announced through <see cref="PeriodSettled"/> whose visible range has not arrived yet.
    /// A range for one of them that is not the latest is a stale answer and must not pull the rail back.
    /// </summary>
    private readonly HashSet<int> _requestedUnits = new();

    private int _latestRequestedUnit;
    private int _announcedViewportUnit = int.MinValue;
    private CalendarRailSpace? _announcedViewportSpace;
    private bool _isGliding;
    private bool _isRenderingHooked;
    private double _glideFrom;
    private int _glideTarget;
    private double _glideDurationMilliseconds;
    private int _lastWheelDirection;
    private bool _isWheelScrolling;
    private double _wheelTargetPosition;

    /// <summary>
    /// Glides to the previous (<paramref name="direction"/> &lt; 0) or next period. Repeated calls
    /// during a glide add up.
    /// </summary>
    public void NavigatePeriods(int direction)
    {
        if (direction == 0 || _space is not CalendarRailSpace space)
        {
            return;
        }

        var baseUnit = _isGliding
            ? _glideTarget
            : CalendarRailMotion.ChooseSnapUnit(space, _isWheelScrolling ? _wheelTargetPosition : _position, 0);

        CancelPan();
        StopWheelScrolling();
        GlideTo(space.MovePeriod(baseUnit, direction), GlideDurationMilliseconds);
    }

    /// <summary>
    /// Reacts to the unit of a visible range the view model published. A confirmation of the period
    /// the rail settled on moves nothing; an external change (today, date picker) glides to it.
    /// </summary>
    private void AcceptVisibleRangeUnit(int targetUnit)
    {
        var isStaleAnswer = _requestedUnits.Contains(targetUnit) && targetUnit != _latestRequestedUnit;

        if (targetUnit == _latestRequestedUnit)
        {
            _requestedUnits.Clear();
        }

        _rangeUnit = targetUnit;

        if (isStaleAnswer || targetUnit == _settledUnit || (_isGliding && _glideTarget == targetUnit))
        {
            return;
        }

        GlideToExternalUnit(targetUnit);
    }

    private void GlideToExternalUnit(int targetUnit)
    {
        if (_space is not CalendarRailSpace space)
        {
            return;
        }

        CancelPan();
        StopWheelScrolling();

        var maxTravel = ExternalGlideMaxTravelPeriods * space.VisibleUnitCount;
        var glideStart = CalendarRailMotion.ClampGlideStart(_position, targetUnit, maxTravel);

        if (Math.Abs(glideStart - _position) > PositionEpsilon)
        {
            StopGlide();
            SetRailPosition(glideStart);
        }

        GlideTo(targetUnit, GlideDurationMilliseconds);
    }

    /// <summary>
    /// Eases the rail from its current position to a snap unit, or jumps there when animations are
    /// turned off.
    /// </summary>
    private void GlideTo(int targetUnit, double durationMilliseconds)
    {
        var canAnimate = _uiSettings.AnimationsEnabled &&
                         IsLoaded &&
                         durationMilliseconds > 0d &&
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
        HookRendering();
    }

    /// <summary>
    /// CompositionTarget.Rendering is a static event XAML cannot attach to. It is hooked only while
    /// the rail moves (a glide, wheel smoothing or a pan) and unhooked when the movement ends and
    /// when the control unloads.
    /// </summary>
    private void HookRendering()
    {
        if (_isRenderingHooked)
        {
            return;
        }

        CompositionTarget.Rendering += RailRendering;
        _isRenderingHooked = true;
    }

    private void UnhookRenderingWhenIdle()
    {
        if (!_isRenderingHooked || _isGliding || _isWheelScrolling || _isPanning)
        {
            return;
        }

        CompositionTarget.Rendering -= RailRendering;
        _isRenderingHooked = false;
    }

    private void RailRendering(object? sender, object e)
    {
        if (_space is null)
        {
            StopGlide();
            StopWheelScrolling();
            return;
        }

        // A pan moves the rail from pointer events; the frame hook stays alive so XAML commits
        // every translation on the next vsync instead of batching it behind input.
        if (_isPanning)
        {
            return;
        }

        if (_isWheelScrolling)
        {
            RenderWheelFrame();
            return;
        }

        if (!_isGliding)
        {
            UnhookRenderingWhenIdle();
            return;
        }

        var progress = _glideClock.Elapsed.TotalMilliseconds / _glideDurationMilliseconds;

        if (progress >= 1d)
        {
            var targetUnit = _glideTarget;

            StopGlide();
            SetRailPosition(targetUnit);
            SettleRail(targetUnit);
            return;
        }

        SetRailPosition(_glideFrom + ((_glideTarget - _glideFrom) * CalendarRailMotion.EaseOut(progress)));
    }

    /// <summary>
    /// Moves the rail towards the accumulated wheel target by a share of the remaining distance
    /// that depends on the frame time, so wheel notches run into one continuous movement whose
    /// speed follows the wheel. Once the target is reached the frames stop; the settle timer then
    /// glides to the nearest snap unit.
    /// </summary>
    private void RenderWheelFrame()
    {
        var elapsedMilliseconds = _wheelClock.Elapsed.TotalMilliseconds;
        var remaining = _wheelTargetPosition - _position;

        _wheelClock.Restart();

        if (Math.Abs(remaining) <= WheelArrivalEpsilon)
        {
            SetRailPosition(_wheelTargetPosition);
            _isWheelScrolling = false;
            UnhookRenderingWhenIdle();
            return;
        }

        var share = 1d - Math.Exp(-elapsedMilliseconds / WheelSmoothingMilliseconds);

        SetRailPosition(_position + (remaining * share));
    }

    private void StopGlide()
    {
        _isGliding = false;
        _glideClock.Stop();
        UnhookRenderingWhenIdle();
    }

    /// <summary>
    /// Drops the wheel target and its settle timer; the rail stays where it is.
    /// </summary>
    private void StopWheelScrolling()
    {
        _wheelSettleTimer.Stop();
        _isWheelScrolling = false;
        _wheelClock.Stop();
        UnhookRenderingWhenIdle();
    }

    private void SetRailPosition(double position)
    {
        _position = position;
        UpdateRailWindow(forceRelayout: false);
        AnnounceViewportPeriod();
    }

    /// <summary>
    /// Tells the page which period sits nearest to the viewport while the rail moves, so the
    /// title follows the rail instead of waiting for the settle.
    /// </summary>
    private void AnnounceViewportPeriod()
    {
        if (_space is not CalendarRailSpace space)
        {
            return;
        }

        var nearestUnit = CalendarRailMotion.ChooseSnapUnit(space, _position, 0);

        if (nearestUnit == _announcedViewportUnit && ReferenceEquals(space, _announcedViewportSpace))
        {
            return;
        }

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

        if (_space is not CalendarRailSpace space || (unit == _rangeUnit && _requestedUnits.Count == 0))
        {
            return;
        }

        _requestedUnits.Add(unit);
        _latestRequestedUnit = unit;

        PeriodSettled?.Invoke(this, new CalendarPeriodEventArgs(space.DisplayType, space.GetAnchorDate(unit)));
    }

    /// <summary>
    /// Pans the rail with the wheel along its axis. The month view takes the vertical wheel; the
    /// timed views take the horizontal wheel or Shift with the vertical wheel and leave the plain
    /// vertical wheel to the hour ScrollViewer.
    /// </summary>
    private void ControlPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_space is not CalendarRailSpace space || _unitSize <= 0d || _isPanning || _rangePointer is not null)
        {
            return;
        }

        var properties = e.GetCurrentPoint(this).Properties;
        var notches = properties.MouseWheelDelta / WheelNotchDelta;
        double unitDelta;

        if (space.Axis == CalendarRailAxis.Vertical)
        {
            if (properties.IsHorizontalMouseWheel)
            {
                return;
            }

            unitDelta = -notches;
        }
        else if (properties.IsHorizontalMouseWheel)
        {
            unitDelta = notches;
        }
        else if ((e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0)
        {
            unitDelta = -notches;
        }
        else
        {
            return;
        }

        if (unitDelta == 0d)
        {
            return;
        }

        unitDelta *= WheelPeriodFractionPerNotch * space.VisibleUnitCount;

        // A glide in progress yields to the wheel from wherever the rail is now; the wheel target
        // accumulates across notches and the rail eases towards it frame by frame.
        if (_isGliding)
        {
            StopGlide();
        }

        if (!_isWheelScrolling)
        {
            _wheelTargetPosition = _position;
            _isWheelScrolling = true;
            _wheelClock.Restart();
        }

        _wheelTargetPosition += unitDelta;
        _lastWheelDirection = Math.Sign(unitDelta);
        HookRendering();

        _wheelSettleTimer.Stop();
        _wheelSettleTimer.Start();
        e.Handled = true;
    }

    /// <summary>
    /// The wheel went quiet: the rail glides from wherever the smoothing has brought it to the
    /// snap unit nearest to the wheel target, biased in the wheel's direction.
    /// </summary>
    private void WheelSettleTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();

        if (_space is not CalendarRailSpace space || _isPanning || _isGliding)
        {
            return;
        }

        var target = CalendarRailMotion.ChooseSnapUnit(space, _isWheelScrolling ? _wheelTargetPosition : _position, _lastWheelDirection);

        _isWheelScrolling = false;
        _wheelClock.Stop();
        GlideTo(target, GlideDurationMilliseconds);
    }
}
