using Wino.Core.Domain.Models.Calendar;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Calendar;

/// <summary>
/// Rail motion (macOS WinoCalendarSurfaceView.Motion, Windows CalendarPeriodControl.Motion): the frame-clock glide,
/// settling on a period, the title announcement while the rail moves, the reaction to a range the ViewModel publishes
/// and the keyboard. Snap targets come from the shared <see cref="CalendarRailMotion"/>.
/// </summary>
public sealed partial class WinoCalendarSurface
{
    private const double GlideDurationMilliseconds = 320;
    private const double ExternalGlideMaxTravelPeriods = 2.5;
    private const double PositionEpsilon = 0.000001;
    private const double WheelPeriodFractionPerNotch = 0.3;
    private const double MaxWheelNotchesPerEvent = 3;
    private const double WheelSmoothingMilliseconds = 70;
    private const double WheelArrivalEpsilon = 0.0005;
    private const uint WheelSettleMilliseconds = 140;

    private bool _isGliding;
    private double _glideFrom;
    private int _glideTarget;
    private double _glideDurationUs;
    private long _glideStartUs = -1;
    private int _announcedViewportUnit = int.MinValue;
    private CalendarRailSpace? _announcedViewportSpace;
    private uint _frameTick;
    private long _lastFrameUs;
    private bool _isWheelScrolling;
    private double _wheelTargetPosition;
    private int _lastWheelDirection;
    private MainLoop.Throttle? _wheelSettle;

    /// <summary>
    /// Glides to the previous (<paramref name="direction"/> &lt; 0) or next period; calls during a glide add up. False
    /// while the surface shows no period.
    /// </summary>
    public bool NavigatePeriods(int direction)
    {
        if (direction == 0 || _space is not { } space) return false;
        var baseUnit = _isGliding
            ? _glideTarget
            : CalendarRailMotion.ChooseSnapUnit(space, _isWheelScrolling ? _wheelTargetPosition : _position, 0);
        CancelPan();
        StopWheelScrolling();
        GlideTo(space.MovePeriod(baseUnit, direction), GlideDurationMilliseconds);
        return true;
    }

    private void GlideToExternalUnit(int targetUnit)
    {
        if (_space is not { } space) return;
        CancelPan();
        StopWheelScrolling();
        var glideStart = CalendarRailMotion.ClampGlideStart(_position, targetUnit, ExternalGlideMaxTravelPeriods * space.VisibleUnitCount);
        if (Math.Abs(glideStart - _position) > PositionEpsilon)
        {
            StopGlide();
            SetRailPosition(glideStart);
        }
        GlideTo(targetUnit, GlideDurationMilliseconds);
    }

    /// <summary>Eases the rail to a snap unit, or jumps there when animations are off, the surface is not mapped or there is no distance.</summary>
    private void GlideTo(int targetUnit, double durationMilliseconds)
    {
        var canAnimate = durationMilliseconds > 0 &&
                         Math.Abs(targetUnit - _position) > PositionEpsilon &&
                         _root.GetMapped() &&
                         AnimationsEnabled(_root);
        if (!canAnimate)
        {
            StopGlide();
            SetRailPosition(targetUnit);
            SettleRail(targetUnit);
            return;
        }
        _glideFrom = _position;
        _glideTarget = targetUnit;
        _glideDurationUs = durationMilliseconds * 1000;
        _glideStartUs = -1;
        _isGliding = true;
        StartFrameDriver();
    }

    /// <summary>One frame of the glide or the wheel smoothing; true while more frames are needed.</summary>
    private bool RailFrame(long frameTimeUs)
    {
        if (_space is null)
        {
            StopGlide();
            StopWheelScrolling();
            _lastFrameUs = frameTimeUs;
            return false;
        }
        if (!_isPanning)
        {
            if (_isWheelScrolling) RenderWheelFrame(frameTimeUs);
            else if (_isGliding)
            {
                if (_glideStartUs < 0) _glideStartUs = frameTimeUs;
                var progress = (frameTimeUs - _glideStartUs) / _glideDurationUs;
                if (progress >= 1)
                {
                    var target = _glideTarget;
                    StopGlide();
                    SetRailPosition(target);
                    SettleRail(target);
                }
                else SetRailPosition(_glideFrom + (_glideTarget - _glideFrom) * CalendarRailMotion.EaseOut(progress));
            }
        }
        _lastFrameUs = frameTimeUs;
        return _isGliding || _isWheelScrolling;
    }

    /// <summary>Closes a share of the remaining distance to the wheel target that depends on the frame time, so notches run into one movement.</summary>
    private void RenderWheelFrame(long frameTimeUs)
    {
        var remaining = _wheelTargetPosition - _position;
        if (Math.Abs(remaining) <= WheelArrivalEpsilon)
        {
            SetRailPosition(_wheelTargetPosition);
            _isWheelScrolling = false;
            return;
        }
        if (_lastFrameUs <= 0 || frameTimeUs <= _lastFrameUs) return;
        var share = 1 - Math.Exp(-(frameTimeUs - _lastFrameUs) / 1000.0 / WheelSmoothingMilliseconds);
        SetRailPosition(_position + remaining * share);
    }

    /// <summary>Drops the wheel target and its settle timer; the rail stays where it is.</summary>
    private void StopWheelScrolling()
    {
        _wheelSettle?.Cancel();
        _isWheelScrolling = false;
    }

    private void StopGlide()
    {
        _isGliding = false;
        _glideStartUs = -1;
    }

    /// <summary>Stops every movement without settling (the rail is rebuilt or the surface leaves the screen).</summary>
    private void StopAllMotion()
    {
        CancelPan();
        _gestureAxis = GestureAxis.None;
        StopWheelScrolling();
        StopGlide();
    }

    /// <summary>Tells the page which period sits nearest to the viewport while the rail moves.</summary>
    private void AnnounceViewportPeriod()
    {
        if (_space is not { } space) return;
        var nearestUnit = CalendarRailMotion.ChooseSnapUnit(space, _position, 0);
        if (nearestUnit == _announcedViewportUnit && ReferenceEquals(space, _announcedViewportSpace)) return;
        _announcedViewportUnit = nearestUnit;
        _announcedViewportSpace = space;
        RaiseViewportPeriodChanged(new CalendarPeriodEventArgs(space.DisplayType, space.GetAnchorDate(nearestUnit)));
    }

    /// <summary>
    /// The frame driver: one tick callback on the root, alive only while a glide or the wheel smoothing runs. The time
    /// base is the frame clock's, so a step is computed for the frame that will be shown.
    /// </summary>
    private void StartFrameDriver()
    {
        if (_frameTick != 0 || _disposed) return;
        var surface = _ref;
        if (_inRelayoutTick)
        {
            // A tick callback added while the surface's own tick callbacks run is never called.
            MainLoop.Post(() =>
            {
                if (surface.Value is { _frameTick: 0 } owner && (owner._isGliding || owner._isWheelScrolling)) owner.StartFrameDriver();
            });
            return;
        }
        _lastFrameUs = 0;
        _frameTick = _root.AddTickCallback((_, clock) =>
        {
            if (surface.Value is not { } owner) return false;
            var now = clock.GetFrameTime();
#if DEBUG
            owner.FrameLog?.Record(now);
#endif
            bool more;
            owner._inRelayoutTick = true;
            try { more = owner.RailFrame(now); }
            finally { owner._inRelayoutTick = false; }
            if (more) return true;
            // The id is cleared before returning false: removing a finished tick callback logs a GLib critical.
            owner._frameTick = 0;
            return false;
        });
    }

    private void StopFrameDriver()
    {
        if (_frameTick == 0) return;
        _root.RemoveTickCallback(_frameTick);
        _frameTick = 0;
    }

    private static bool AnimationsEnabled(Gtk.Widget widget)
        => (Gtk.Settings.GetForDisplay(widget.GetDisplay()) ?? Gtk.Settings.GetDefault())?.GtkEnableAnimations != false;

    /// <summary>The surface left the screen: a press ends, and a glide lands on its target so the rail and the loaded period agree.</summary>
    partial void OnUnmapped()
    {
        CancelPress();
        var wasGliding = _isGliding;
        var target = _glideTarget;
        StopAllMotion();
        StopFrameDriver();
        if (!wasGliding) return;
        SetRailPosition(target);
        SettleRail(target);
    }

    private void AttachKeys()
    {
        var keys = Gtk.EventControllerKey.New();
        _root.AddController(keys);
        Own(GtkSignals.ConnectReturning<Gtk.EventControllerKey, Gtk.EventControllerKey.KeyPressedSignalArgs, bool>(
            h => keys.OnKeyPressed += h, h => keys.OnKeyPressed -= h,
            (_, args) => HandleKey(args.Keyval, args.State), false));
    }

    /// <summary>Left/Right (Up/Down in Month) move by whole periods, Home asks for today, Esc cancels a press (macOS KeyDown); Ctrl+C and Ctrl+V ask the page to copy the selected event and paste it.</summary>
    private bool HandleKey(uint keyval, Gdk.ModifierType state)
    {
        if (keyval == Gdk.Constants.KEY_Escape && _press is not null)
        {
            CancelPress();
            return true;
        }
        if (keyval == Gdk.Constants.KEY_Menu || (keyval == Gdk.Constants.KEY_F10 && (state & Gdk.ModifierType.ShiftMask) != 0)) return RaiseKeyboardSlotContext();
        if ((state & (Gdk.ModifierType.ControlMask | Gdk.ModifierType.ShiftMask | Gdk.ModifierType.AltMask | Gdk.ModifierType.SuperMask)) == Gdk.ModifierType.ControlMask)
        {
            if (keyval is Gdk.Constants.KEY_c or Gdk.Constants.KEY_C)
            {
                CopyRequested?.Invoke(this, EventArgs.Empty);
                return CopyRequested is not null;
            }
            if (keyval is Gdk.Constants.KEY_v or Gdk.Constants.KEY_V) return RaiseKeyboardPaste();
        }
        if ((state & (Gdk.ModifierType.ControlMask | Gdk.ModifierType.AltMask | Gdk.ModifierType.SuperMask)) != 0 || _space is not { } space) return false;
        var month = space.Axis == CalendarRailAxis.Vertical;
        var direction = keyval switch
        {
            Gdk.Constants.KEY_Left or Gdk.Constants.KEY_KP_Left when !month => -1,
            Gdk.Constants.KEY_Right or Gdk.Constants.KEY_KP_Right when !month => 1,
            Gdk.Constants.KEY_Up or Gdk.Constants.KEY_KP_Up when month => -1,
            Gdk.Constants.KEY_Down or Gdk.Constants.KEY_KP_Down when month => 1,
            _ => 0
        };
        if (direction != 0)
        {
            NavigatePeriods(direction);
            return true;
        }
        if (keyval is Gdk.Constants.KEY_Home or Gdk.Constants.KEY_KP_Home)
        {
            RaiseTodayRequested();
            return true;
        }
        return false;
    }

#if DEBUG
    public double DebugRailPosition => _position;

    public bool DebugIsGliding => _isGliding;

    /// <summary>Puts the rail at <paramref name="position"/> without settling.</summary>
    public void DebugSetRailPosition(double position)
    {
        StopAllMotion();
        SetRailPosition(position);
    }

    /// <summary>Glides to the nearest snap unit, like a release without velocity.</summary>
    public void DebugSettleRail()
    {
        if (_space is not { } space) return;
        GlideTo(CalendarRailMotion.ChooseSnapUnit(space, _position, 0), GlideDurationMilliseconds);
    }

    public bool DebugKey(uint keyval, Gdk.ModifierType state) => HandleKey(keyval, state);
#endif
}
