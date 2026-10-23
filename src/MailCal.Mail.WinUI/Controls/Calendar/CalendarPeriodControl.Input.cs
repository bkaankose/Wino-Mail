using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using MailCal.Core.Domain.Models.Calendar;

namespace MailCal.Calendar.Controls;

/// <summary>
/// Pointer panning with mouse, pen or touch. A press anywhere on the surface, except on an event
/// or with Shift and the left button (range selection), becomes a pan once it moves 6 px along the
/// rail axis more than across it. Until then taps work and the hour ScrollViewer keeps scrolling.
/// </summary>
public sealed partial class CalendarPeriodControl
{
    private const double PanStartThreshold = 6d;
    private const double FlickVelocityThresholdPixelsPerMillisecond = 0.35d;
    private const double PanVelocitySmoothing = 0.6d;
    private const double ReleaseVelocityTimeoutMilliseconds = 100d;
    private const double MinReleaseGlideMilliseconds = 180d;
    private const double MaxReleaseGlideMilliseconds = 520d;

    private Pointer? _panPointer;
    private Point _panPressPoint;
    private bool _isPanning;
    private double _panStartPosition;
    private int _panStartUnit;
    private double _panLastAlong;
    private ulong _panLastTimestamp;
    private double _panVelocity;

    private void ControlPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (TryBeginRangeSelection(e))
            return;

        TryBeginPanCandidate(e);
    }

    private void ControlPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (TryUpdateRangeSelection(e))
            return;

        if (_panPointer?.PointerId != e.Pointer.PointerId || _space is not CalendarRailSpace space || _unitSize <= 0d)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        var (along, across) = GetAxisDeltas(space, point.Position);

        if (!_isPanning)
        {
            if (Math.Abs(along) < PanStartThreshold)
            {
                if (Math.Abs(across) >= PanStartThreshold)
                {
                    ClearPanState();
                }

                return;
            }

            if (Math.Abs(along) <= Math.Abs(across) || !TryBeginPan(e.Pointer, space, along, point.Timestamp))
            {
                ClearPanState();
                return;
            }
        }

        UpdatePan(along, point.Timestamp);
        e.Handled = true;
    }

    private void ControlPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (TryCompleteRangeSelection(e))
            return;

        if (_panPointer?.PointerId != e.Pointer.PointerId)
        {
            return;
        }

        if (!_isPanning || _space is not CalendarRailSpace space)
        {
            ClearPanState();
            return;
        }

        var point = e.GetCurrentPoint(this);
        var (along, _) = GetAxisDeltas(space, point.Position);
        var idleMilliseconds = point.Timestamp > _panLastTimestamp ? (point.Timestamp - _panLastTimestamp) / 1000d : 0d;

        UpdatePan(along, point.Timestamp);

        // A pointer that rested before release carries no flick.
        var velocity = idleMilliseconds > ReleaseVelocityTimeoutMilliseconds ? 0d : _panVelocity;

        // Dragging towards the start of the rail (left or up) moves to later units.
        var target = CalendarRailMotion.ChooseReleaseUnit(
            space,
            _position,
            _panStartUnit,
            -velocity / _unitSize,
            FlickVelocityThresholdPixelsPerMillisecond / _unitSize);

        CancelPan();
        GlideTo(target, GetReleaseGlideDuration(target, velocity));
        e.Handled = true;
    }

    private void ControlPointerCanceled(object sender, PointerRoutedEventArgs e)
        => AbandonPointer(e.Pointer.PointerId);

    private void ControlPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => AbandonPointer(e.Pointer.PointerId);

    private void AbandonPointer(uint pointerId)
    {
        if (_rangePointer?.PointerId == pointerId)
            CancelRangeSelection();

        if (_panPointer?.PointerId != pointerId)
        {
            return;
        }

        var wasPanning = _isPanning;

        CancelPan();

        if (wasPanning && _space is CalendarRailSpace space)
        {
            GlideTo(CalendarRailMotion.ChooseSnapUnit(space, _position, 0), GlideDurationMilliseconds);
        }
    }

    private void TryBeginPanCandidate(PointerRoutedEventArgs e)
    {
        // A pending press that never reported its release is replaced; an active pan is not.
        if (_isPanning || _space is null || _unitSize <= 0d || IsCalendarItemSource(e.OriginalSource))
        {
            return;
        }

        ClearPanState();

        var point = e.GetCurrentPoint(this);

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse &&
            (!point.Properties.IsLeftButtonPressed || (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0))
        {
            return;
        }

        _panPointer = e.Pointer;
        _panPressPoint = point.Position;
        _isPanning = false;

        Focus(FocusState.Pointer);
    }

    private bool TryBeginPan(Pointer pointer, CalendarRailSpace space, double along, ulong timestamp)
    {
        if (!CapturePointer(pointer))
        {
            return false;
        }

        StopGlide();
        StopWheelScrolling();
        SetHoverTarget(null);

        _isPanning = true;
        _panStartPosition = _position;
        _panStartUnit = CalendarRailMotion.ChooseSnapUnit(space, _position, 0);
        _panLastAlong = along;
        _panLastTimestamp = timestamp;
        _panVelocity = 0d;

        // The press turned into a pan, so the empty-slot tap it would otherwise produce is dropped.
        _suppressRangeTap = true;

        // Pointer events alone let XAML batch the rail's translation behind input; keeping the frame
        // hook alive for the pan makes every move land on the next vsync, so small drags stay fluent.
        HookRendering();

        return true;
    }

    private void UpdatePan(double along, ulong timestamp)
    {
        if (timestamp > _panLastTimestamp)
        {
            var elapsedMilliseconds = (timestamp - _panLastTimestamp) / 1000d;
            var sample = (along - _panLastAlong) / elapsedMilliseconds;

            _panVelocity = (_panVelocity * (1d - PanVelocitySmoothing)) + (sample * PanVelocitySmoothing);
            _panLastTimestamp = timestamp;
        }

        _panLastAlong = along;

        SetRailPosition(_panStartPosition - (along / _unitSize));
    }

    /// <summary>
    /// Ends a pan or a pending press without gliding. The capture is released after the state is
    /// cleared, so the capture-lost notification it raises finds nothing to do.
    /// </summary>
    private void CancelPan()
    {
        var pointer = _panPointer;
        var wasPanning = _isPanning;

        ClearPanState();
        UnhookRenderingWhenIdle();

        if (wasPanning && pointer is not null)
        {
            ReleasePointerCapture(pointer);
        }
    }

    private void ClearPanState()
    {
        _panPointer = null;
        _isPanning = false;
        _panVelocity = 0d;
    }

    private (double Along, double Across) GetAxisDeltas(CalendarRailSpace space, Point position)
    {
        var deltaX = position.X - _panPressPoint.X;
        var deltaY = position.Y - _panPressPoint.Y;

        return space.Axis == CalendarRailAxis.Horizontal
            ? (deltaX, deltaY)
            : (deltaY, deltaX);
    }

    /// <summary>
    /// A glide whose ease-out starts at the release speed: a cubic ease-out starts at three times
    /// its average speed. Slow releases use the standard duration; both are clamped.
    /// </summary>
    private double GetReleaseGlideDuration(int targetUnit, double velocityPixelsPerMillisecond)
    {
        var distance = Math.Abs(targetUnit - _position) * _unitSize;
        var speed = Math.Abs(velocityPixelsPerMillisecond);
        var duration = speed > 0.05d ? 3d * distance / speed : GlideDurationMilliseconds;

        return Math.Clamp(duration, MinReleaseGlideMilliseconds, MaxReleaseGlideMilliseconds);
    }
}
