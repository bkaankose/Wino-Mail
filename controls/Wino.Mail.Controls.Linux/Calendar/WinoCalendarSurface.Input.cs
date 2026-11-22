using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Core.Calendar;
using Wino.Mail.Controls.Core.Layout;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Calendar;

/// <summary>
/// Presses on empty surface space and the pans they become (macOS WinoCalendarSurfaceView.Selection and .Motion,
/// Windows CalendarPeriodControl.Input): one drag gesture per strip routes a press to a click, a pan once it moves
/// along the rail, or the range selection hooks; the scroll controller on the root pans with the wheel and the
/// touchpad before the hour grid scrolls.
/// </summary>
public sealed partial class WinoCalendarSurface
{
    private const double PanStartThreshold = 6;
    private const double FlickVelocityThresholdPointsPerMillisecond = 0.35;
    private const double PanVelocitySmoothing = 0.6;
    private const double ReleaseVelocityTimeoutMilliseconds = 100;
    private const double MinReleaseGlideMilliseconds = 180;
    private const double MaxReleaseGlideMilliseconds = 520;
    private const double GestureAxisLockDistance = 4;

    private bool _isPanning;
    private double _panStartPosition;
    private int _panStartUnit;
    private double _panLastAlong;
    private double _panLastTimeMs;
    private double _panVelocity;
    private SlotPress? _press;
    private GestureAxis _gestureAxis;
    private double _gestureX;
    private double _gestureY;
    private bool _gestureWithoutBegin;
    private DateOnly? _lastMonthClickDay;
    private uint _lastMonthClickTime;

    /// <summary>What a touchpad gesture drives: undecided until it travels a few pixels, then the rail or the hour grid (Pass).</summary>
    private enum GestureAxis { None, Undecided, Rail, Pass }

    /// <summary>One press on empty surface space: a click, a pan once it moves along the rail, or (CA-07) a range drag.</summary>
    private sealed class SlotPress(CalendarStrip strip, DateTime? anchorCell, double downX, double downY, bool shift, Action? click)
    {
        public CalendarStrip Strip { get; } = strip;

        /// <summary>The snapped cell under the press; null on the gutter and the header, where a press only clicks or pans.</summary>
        public DateTime? AnchorCell { get; } = anchorCell;
        public double DownX { get; } = downX;
        public double DownY { get; } = downY;
        public bool Shift { get; } = shift;
        public Action? Click { get; } = click;
        public bool Panning { get; set; }

        /// <summary>Set by a selection hook that took the press; the router then stops testing for a pan.</summary>
        public bool Handled { get; set; }
        public object? Tag { get; set; }
    }

    private int SnapMinutes => 15;

    partial void OnSlotPressMoved(SlotPress press, double stripX, double stripY, ref bool handled);
    partial void OnSlotPressReleased(SlotPress press, double stripX, double stripY, ref bool handled);
    partial void OnSlotPressCancelled(SlotPress press);
    partial void ResolveMonthPress(double railX, double railY, ref DateTime? cell, ref Action? click);

    partial void AttachInput()
    {
        AttachStripInput(CalendarStrip.Header);
        AttachStripInput(CalendarStrip.AllDay);
        AttachStripInput(CalendarStrip.Timed);
        AttachStripInput(CalendarStrip.Month);

        // Capture phase: the rail sees a wheel event before the hour grid's scrolled window (rail contract point 7).
        var scroll = Gtk.EventControllerScroll.New(Gtk.EventControllerScrollFlags.BothAxes);
        scroll.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        _root.AddController(scroll);
        Own(GtkSignals.ConnectReturning<Gtk.EventControllerScroll, Gtk.EventControllerScroll.ScrollSignalArgs, bool>(
            h => scroll.OnScroll += h, h => scroll.OnScroll -= h,
            (controller, args) => HandleScroll(args.Dx, args.Dy, controller.GetUnit(), controller.GetCurrentEventState(), controller.GetCurrentEventTime()), false));
        Own(GtkSignals.Connect<Gtk.EventControllerScroll>(h => scroll.OnScrollBegin += h, h => scroll.OnScrollBegin -= h, (_, _) => HandleScrollBegin()));
        Own(GtkSignals.Connect<Gtk.EventControllerScroll>(h => scroll.OnScrollEnd += h, h => scroll.OnScrollEnd -= h,
            (controller, _) => HandleScrollEnd(controller.GetCurrentEventTime())));
    }

    /// <summary>One drag gesture on the strip, bubble phase: a gesture of a tile that claims its press keeps it from the strip.</summary>
    internal void AttachStripInput(CalendarStrip strip)
    {
        var widget = StripWidget(strip);
        var drag = Gtk.GestureDrag.New();
        drag.SetButton(1);
        widget.AddController(drag);
        Own(GtkSignals.Connect<Gtk.GestureDrag, Gtk.GestureDrag.DragBeginSignalArgs>(h => drag.OnDragBegin += h, h => drag.OnDragBegin -= h,
            (gesture, args) => BeginPress(strip, args.StartX, args.StartY, (gesture.GetCurrentEventState() & Gdk.ModifierType.ShiftMask) != 0)));
        Own(GtkSignals.Connect<Gtk.GestureDrag, Gtk.GestureDrag.DragUpdateSignalArgs>(h => drag.OnDragUpdate += h, h => drag.OnDragUpdate -= h,
            (gesture, args) => UpdatePress(gesture, args.OffsetX, args.OffsetY, gesture.GetCurrentEventTime())));
        Own(GtkSignals.Connect<Gtk.GestureDrag, Gtk.GestureDrag.DragEndSignalArgs>(h => drag.OnDragEnd += h, h => drag.OnDragEnd -= h,
            (gesture, args) => EndPress(args.OffsetX, args.OffsetY, gesture.GetCurrentEventTime())));
        Own(GtkSignals.Connect<Gtk.Gesture, Gtk.Gesture.CancelSignalArgs>(h => drag.OnCancel += h, h => drag.OnCancel -= h, (_, _) => CancelPress()));
    }

    /// <summary>Resolves what the press would click (a slot, an all-day day, a month day) and its range anchor cell.</summary>
    private void BeginPress(CalendarStrip strip, double x, double y, bool shift)
    {
        CancelPress();
        FocusSurface();
        var (railX, railY) = ToRail(strip, x, y);
        DateTime? anchor = null;
        Action? click = null;
        var dates = _windowDates;
        var dayWidth = DayWidth;
        switch (strip)
        {
            case CalendarStrip.Timed when x >= HourColumnWidth && WinoCalendarSurfaceGeometry.SlotStartAt(railX, railY, dates, dayWidth, HourHeight) is { } start:
            {
                var hourHeight = HourHeight;
                anchor = WinoCalendarSurfaceGeometry.TimedCellAt(railX, railY, dates, dayWidth, hourHeight, SnapMinutes);
                var dayIndex = Math.Clamp((int)((railX - HourColumnWidth) / dayWidth), 0, dates.Count - 1);
                var bounds = WinoCalendarSurfaceGeometry.SlotBounds(dayIndex, WinoCalendarSurfaceGeometry.SlotAt(railY, hourHeight), dayWidth, hourHeight);
                click = () => RaiseSlotClicked(start, bounds, strip, isAllDay: false);
                break;
            }
            case CalendarStrip.AllDay when x >= HourColumnWidth && dates.Count > 0 && dayWidth > 0:
            {
                var dayIndex = WinoCalendarSurfaceGeometry.DayIndexAt(railX, dates.Count, dayWidth);
                var bounds = new WinoLayoutFrame(WinoCalendarSurfaceGeometry.DayX(dayIndex, dayWidth), 0, dayWidth, Math.Max(1, _allDayOverlay.GetHeight()));
                var date = dates[dayIndex].ToDateTime(TimeOnly.MinValue);
                click = () => RaiseSlotClicked(date, bounds, strip, isAllDay: true);
                break;
            }
            case CalendarStrip.Month:
                ResolveMonthPress(railX, railY, ref anchor, ref click);
                break;
        }
        _press = new SlotPress(strip, anchor, x, y, shift, click);
    }

    private void UpdatePress(Gtk.Gesture? gesture, double offsetX, double offsetY, uint timeMs)
    {
        if (_press is not { } press) return;
        var (along, across) = IsMonth ? (offsetY, offsetX) : (offsetX, offsetY);
        if (press.Panning)
        {
            UpdatePan(along, timeMs);
            return;
        }
        if (!press.Handled && !press.Shift && _space is not null && _unitSize > 0 && Math.Abs(along) >= PanStartThreshold && Math.Abs(along) > Math.Abs(across))
        {
            press.Panning = true;
            gesture?.SetState(Gtk.EventSequenceState.Claimed);
            BeginPan(along, timeMs);
            return;
        }
        var handled = press.Handled;
        OnSlotPressMoved(press, press.DownX + offsetX, press.DownY + offsetY, ref handled);
        press.Handled = handled;
    }

    private void EndPress(double offsetX, double offsetY, uint timeMs)
    {
        if (_press is not { } press) return;
        _press = null;
        if (press.Panning)
        {
            EndPan(IsMonth ? offsetY : offsetX, timeMs);
            return;
        }
        var handled = false;
        OnSlotPressReleased(press, press.DownX + offsetX, press.DownY + offsetY, ref handled);
        if (handled) return;
        if (press.Strip == CalendarStrip.Month && press.AnchorCell is { } cell && IsMonthDoubleClick(DateOnly.FromDateTime(cell), timeMs))
        {
            MonthDayDoubleClicked?.Invoke(this, DateOnly.FromDateTime(cell));
            return;
        }
        press.Click?.Invoke();
    }

    /// <summary>
    /// The second click on the same month day within the double-click time. The strip's drag gesture counts no presses, so
    /// the release times are compared; the first click has already opened the quick event, which the page closes (macOS).
    /// </summary>
    private bool IsMonthDoubleClick(DateOnly day, uint timeMs)
    {
        var settings = Gtk.Settings.GetForDisplay(_root.GetDisplay()) ?? Gtk.Settings.GetDefault();
        var limit = settings?.GtkDoubleClickTime is > 0 and var time ? (uint)time : 400u;
        var isDouble = _lastMonthClickDay == day && timeMs >= _lastMonthClickTime && timeMs - _lastMonthClickTime <= limit;
        _lastMonthClickDay = isDouble ? null : day;
        _lastMonthClickTime = timeMs;
        return isDouble;
    }

    /// <summary>Ends a press without a click (gesture cancelled, Esc, unmap, a popover opened); an interrupted pan glides to the nearest snap unit.</summary>
    public void CancelPress()
    {
        if (_press is not { } press) return;
        _press = null;
        var wasPanning = press.Panning && _isPanning;
        CancelPan();
        OnSlotPressCancelled(press);
        if (wasPanning && _space is { } space) GlideTo(CalendarRailMotion.ChooseSnapUnit(space, _position, 0), GlideDurationMilliseconds);
    }

    private void RaiseSlotClicked(DateTime start, WinoLayoutFrame railBounds, CalendarStrip strip, bool isAllDay)
    {
        var (x, y) = ToStrip(strip, railBounds.X, railBounds.Y);
        var anchor = new Gdk.Rectangle
        {
            X = (int)Math.Round(x),
            Y = (int)Math.Round(y),
            Width = Math.Max(1, (int)Math.Round(railBounds.Width)),
            Height = Math.Max(1, (int)Math.Round(railBounds.Height))
        };
        RaiseSlotClicked(new CalendarSlotEventArgs(start, null, isAllDay, StripWidget(strip), anchor));
    }

    // ---------------------------------------------------------------- click-drag pan

    private void BeginPan(double along, uint timeMs)
    {
        if (_space is not { } space) return;
        StopGlide();
        StopWheelScrolling();
        CloseOverflow();
        _isPanning = true;
        _panStartPosition = _position;
        _panStartUnit = CalendarRailMotion.ChooseSnapUnit(space, _position, 0);
        _panLastAlong = along;
        _panLastTimeMs = timeMs;
        _panVelocity = 0;
        _root.SetCursorFromName("grabbing");
        UpdatePan(along, timeMs);
    }

    /// <summary>The content follows the pointer: dragging right (or down) moves the rail to earlier units.</summary>
    private void UpdatePan(double along, uint timeMs)
    {
        if (!_isPanning || _unitSize <= 0) return;
        if (timeMs > _panLastTimeMs)
        {
            var sample = (along - _panLastAlong) / (timeMs - _panLastTimeMs);
            _panVelocity = _panVelocity * (1 - PanVelocitySmoothing) + sample * PanVelocitySmoothing;
            _panLastTimeMs = timeMs;
        }
        _panLastAlong = along;
        SetRailPosition(_panStartPosition - along / _unitSize);
    }

    private void EndPan(double along, uint timeMs)
    {
        if (!_isPanning || _space is not { } space)
        {
            CancelPan();
            return;
        }
        var idle = timeMs - _panLastTimeMs;
        UpdatePan(along, timeMs);
        var velocity = idle > ReleaseVelocityTimeoutMilliseconds ? 0 : _panVelocity;
        CancelPan();
        Release(space, -velocity);
    }

    /// <summary>Ends a pan without gliding.</summary>
    private void CancelPan()
    {
        if (!_isPanning) return;
        _isPanning = false;
        _panVelocity = 0;
        _root.SetCursor(null);
    }

    /// <summary>Glides from a released pan or touchpad gesture; the velocity is in pixels per millisecond towards later units.</summary>
    private void Release(CalendarRailSpace space, double velocityTowardsLater)
    {
        var target = CalendarRailMotion.ChooseReleaseUnit(space, _position, _panStartUnit,
            velocityTowardsLater / _unitSize, FlickVelocityThresholdPointsPerMillisecond / _unitSize);
        GlideTo(target, GetReleaseGlideDuration(target, velocityTowardsLater));
    }

    /// <summary>A cubic ease-out starts at three times its average speed, so the glide starts at the release speed; clamped.</summary>
    private double GetReleaseGlideDuration(int targetUnit, double velocityPointsPerMillisecond)
    {
        var distance = Math.Abs(targetUnit - _position) * _unitSize;
        var speed = Math.Abs(velocityPointsPerMillisecond);
        var duration = speed > 0.05 ? 3 * distance / speed : GlideDurationMilliseconds;
        return Math.Clamp(duration, MinReleaseGlideMilliseconds, MaxReleaseGlideMilliseconds);
    }

    // ---------------------------------------------------------------- wheel and touchpad

    /// <summary>
    /// The wheel and the touchpad along the rail axis: Month takes vertical scrolling, the timed views horizontal
    /// scrolling or Shift with a vertical wheel. Everything else is left to the hour grid. True when consumed.
    /// </summary>
    private bool HandleScroll(double dx, double dy, Gdk.ScrollUnit unit, Gdk.ModifierType state, uint timeMs)
    {
        if (_space is not { } space || _unitSize <= 0 || _rangeSelectionActive || _tileDragActive) return false;
        if (unit == Gdk.ScrollUnit.Wheel)
        {
            _gestureAxis = GestureAxis.None;
            return HandleWheel(space, dx, dy, (state & Gdk.ModifierType.ShiftMask) != 0);
        }

        if (_gestureAxis == GestureAxis.None)
        {
            // Some drivers send surface-unit scrolling without scroll-begin; such a run ends through the settle timer.
            HandleScrollBegin();
            _gestureWithoutBegin = true;
        }
        if (_gestureWithoutBegin) WheelSettle.Request();

        var horizontalRail = space.Axis == CalendarRailAxis.Horizontal;
        switch (_gestureAxis)
        {
            case GestureAxis.Undecided:
            {
                _gestureX += dx;
                _gestureY += dy;
                var along = Math.Abs(horizontalRail ? _gestureX : _gestureY);
                var across = Math.Abs(horizontalRail ? _gestureY : _gestureX);
                // GTK cannot replay held events: undecided travel goes to the hour grid on the horizontal rail (it ignores x)
                // and is swallowed on the vertical rail.
                if (Math.Max(along, across) < GestureAxisLockDistance) return !horizontalRail;
                if (along > across)
                {
                    _gestureAxis = GestureAxis.Rail;
                    BeginGesturePan(space, timeMs);
                    UpdateGesturePan(horizontalRail ? _gestureX : _gestureY, timeMs);
                    return true;
                }
                _gestureAxis = GestureAxis.Pass;
                return false;
            }
            case GestureAxis.Rail:
                UpdateGesturePan(horizontalRail ? dx : dy, timeMs);
                return true;
            default:
                return false;
        }
    }

    private void HandleScrollBegin()
    {
        _gestureX = _gestureY = 0;
        _gestureWithoutBegin = false;
        _gestureAxis = _isPanning || _press is { Panning: true } ? GestureAxis.Pass : GestureAxis.Undecided;
    }

    private void HandleScrollEnd(uint timeMs)
    {
        if (_gestureAxis == GestureAxis.Rail && _space is { } space) EndGesturePan(space, timeMs);
        _gestureAxis = GestureAxis.None;
        _gestureWithoutBegin = false;
    }

    private void BeginGesturePan(CalendarRailSpace space, uint timeMs)
    {
        CancelPress();
        StopGlide();
        StopWheelScrolling();
        _panStartUnit = CalendarRailMotion.ChooseSnapUnit(space, _position, 0);
        _panLastTimeMs = timeMs;
        _panVelocity = 0;
    }

    /// <summary>Touchpad deltas scroll towards later content when positive, so the rail moves with them.</summary>
    private void UpdateGesturePan(double delta, uint timeMs)
    {
        if (timeMs > _panLastTimeMs)
        {
            var sample = delta / (timeMs - _panLastTimeMs);
            _panVelocity = _panVelocity * (1 - PanVelocitySmoothing) + sample * PanVelocitySmoothing;
            _panLastTimeMs = timeMs;
        }
        SetRailPosition(_position + delta / _unitSize);
    }

    private void EndGesturePan(CalendarRailSpace space, uint timeMs)
    {
        var idle = timeMs - _panLastTimeMs;
        Release(space, idle > ReleaseVelocityTimeoutMilliseconds ? 0 : _panVelocity);
    }

    private MainLoop.Throttle WheelSettle => _wheelSettle ??= CreateWheelSettle();

    private MainLoop.Throttle CreateWheelSettle()
    {
        var surface = _ref;
        return new MainLoop.Throttle(WheelSettleMilliseconds, () => surface.Value?.WheelSettled());
    }

    /// <summary>
    /// A wheel notch moves 30% of a period; notches add up to a target the rail eases to, and once the wheel is quiet for
    /// 140 ms the rail glides to the snap unit nearest the target, biased in the wheel's direction.
    /// </summary>
    private bool HandleWheel(CalendarRailSpace space, double dx, double dy, bool shift)
    {
        if (_isPanning || _press is { Panning: true }) return false;
        double delta;
        if (space.Axis == CalendarRailAxis.Vertical)
        {
            if (dy == 0) return false;
            delta = dy;
        }
        else if (dx != 0) delta = dx;
        else if (shift && dy != 0) delta = dy;
        else return false;

        var unitDelta = Math.Clamp(delta, -MaxWheelNotchesPerEvent, MaxWheelNotchesPerEvent) * WheelPeriodFractionPerNotch * space.VisibleUnitCount;
        if (_isGliding) StopGlide();
        if (!_isWheelScrolling)
        {
            _wheelTargetPosition = _position;
            _isWheelScrolling = true;
        }
        _wheelTargetPosition += unitDelta;
        _lastWheelDirection = Math.Sign(unitDelta);
        StartFrameDriver();
        WheelSettle.Request();
        return true;
    }

    private void WheelSettled()
    {
        if (_gestureWithoutBegin)
        {
            HandleScrollEnd(_panLastTimeMs > uint.MaxValue ? 0 : (uint)_panLastTimeMs + (uint)WheelSettleMilliseconds);
            return;
        }
        if (_space is not { } space || _isPanning || _isGliding) return;
        var target = CalendarRailMotion.ChooseSnapUnit(space, _isWheelScrolling ? _wheelTargetPosition : _position, _lastWheelDirection);
        _isWheelScrolling = false;
        GlideTo(target, GlideDurationMilliseconds);
    }

#if DEBUG
    /// <summary>A scripted drag pan of <paramref name="units"/> (positive = dragged right or down) released at <paramref name="velocity"/> px/ms; returns the release target.</summary>
    public string DebugPan(double units, double velocity)
    {
        if (_space is null || _unitSize <= 0) return "no rail";
        const uint start = 1_000_000;
        var before = _position;
        BeginPan(0, start);
        for (var step = 1; step <= 10; step++) UpdatePan(units * _unitSize * step / 10, start + (uint)(16 * step));
        var followed = _position;
        _panVelocity = velocity;
        EndPan(units * _unitSize, start + 160);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"before={before:0.###} followed={followed:0.###} target={(_isGliding ? _glideTarget : _settledUnit)} gliding={_isGliding}");
    }

    public bool DebugWheel(double notches, bool shift)
        => HandleScroll(0, notches, Gdk.ScrollUnit.Wheel, shift ? Gdk.ModifierType.ShiftMask : 0, 0);

    /// <summary>A press and release without movement at strip coordinates (a click); returns what it raised.</summary>
    public string DebugPress(string strip, double x, double y)
    {
        if (!Enum.TryParse<CalendarStrip>(strip, true, out var target)) return "unknown strip";
        string raised = "nothing";
        EventHandler<CalendarSlotEventArgs> handler = (_, args) => raised = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"SlotClicked start={args.Start:yyyy-MM-dd HH:mm} allDay={args.IsAllDay} anchor=({args.Anchor.X},{args.Anchor.Y},{args.Anchor.Width},{args.Anchor.Height}) widget={args.AnchorWidget.GetType().Name}");
        SlotClicked += handler;
        try
        {
            BeginPress(target, x, y, false);
            var anchor = _press?.AnchorCell;
            EndPress(0, 0, 0);
            return $"{raised} anchorCell={anchor:yyyy-MM-dd HH:mm}";
        }
        finally { SlotClicked -= handler; }
    }
#endif
}
