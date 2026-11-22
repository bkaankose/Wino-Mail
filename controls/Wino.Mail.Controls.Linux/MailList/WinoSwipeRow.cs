using System.Diagnostics;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.MailList;

/// <summary>Leading: the row moves right and uncovers its left side.</summary>
public enum WinoSwipeSide
{
    None,
    Leading,
    Trailing,
}

public enum WinoSwipeTone
{
    Accent,
    Destructive,
    Success,
    Warning,
}

public readonly record struct WinoSwipeAction(WinoIconGlyph Glyph, string Label, WinoSwipeTone Tone);

/// <summary>
/// A row that slides sideways under a finger or a two-finger touchpad swipe and reveals the action of that side
/// (Windows <c>WinoSwipeControl</c>, macOS row actions). Releasing past the threshold commits the action.
/// </summary>
public sealed class WinoSwipeRow : IDisposable
{
    private const double StartDistance = 12;
    private const double TouchpadLock = 8;
    private const uint CommitDuration = 120;
    private const uint ReturnDuration = 150;

    private static readonly string[] ToneClasses = [WinoCssClass.SwipeAccent, WinoCssClass.SwipeDestructive, WinoCssClass.SwipeSuccess, WinoCssClass.SwipeWarning];

    private readonly Gtk.Overlay _root;
    private readonly Gtk.Box _backdrop;
    private readonly Gtk.Box _action;
    private readonly Gtk.Label _glyph;
    private readonly Gtk.Label _title;
    private readonly Gtk.Fixed _stage;
    private readonly Gtk.Box _card;
    private readonly Gtk.Picture _picture;
    private readonly Gtk.GestureDrag _drag;
    private readonly Gtk.EventControllerScroll _scroll;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Stopwatch _clock = new();
    private double _offset;
    private double _width;
    private bool _armed;
    private bool _touchDecided;
    private bool _touchpad;
    private int _touchpadLock;
    private double _scrollX;
    private double _scrollY;
    private uint _motion;
    private bool _closing;
    private bool _disposed;

    public WinoSwipeRow(Gtk.Widget content)
    {
        WinoLiveObjects.Track(this);
        Content = content;
        _root = Gtk.Overlay.New();
        _root.AddCssClass(WinoCssClass.SwipeRow);
        _root.SetOverflow(Gtk.Overflow.Hidden);
        _root.SetChild(content);

        _backdrop = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        _backdrop.AccessibleRole = Gtk.AccessibleRole.Presentation;
        _backdrop.AddCssClass(WinoCssClass.SwipeBackdrop);
        _action = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        _action.SetValign(Gtk.Align.Center);
        _action.SetHexpand(true);
        _action.SetMarginStart(20);
        _action.SetMarginEnd(20);
        _glyph = WinoIcons.Label(string.Empty, 18);
        _title = Gtk.Label.New(null);
        _action.Append(_glyph);
        _action.Append(_title);
        _backdrop.Append(_action);
        _backdrop.SetVisible(false);
        _backdrop.SetCanTarget(false);

        // A GTK widget cannot be translated without a custom layout (a widget subclass), so a live picture of the row
        // slides over an opaque backdrop while the row itself stays where it is.
        _stage = Gtk.Fixed.New();
        _stage.AccessibleRole = Gtk.AccessibleRole.Presentation;
        _stage.SetCanTarget(false);
        _stage.SetVisible(false);
        _card = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        _card.AddCssClass(WinoCssClass.SwipeCard);
        _picture = Gtk.Picture.NewForPaintable(Gtk.WidgetPaintable.New(content));
        _picture.SetCanShrink(false);
        _picture.SetHexpand(true);
        _card.Append(_picture);
        _stage.Put(_card, 0, 0);

        _root.AddOverlay(_backdrop);
        _root.AddOverlay(_stage);
        _root.SetMeasureOverlay(_backdrop, false);
        _root.SetMeasureOverlay(_stage, false);

        _drag = Gtk.GestureDrag.New();
        _drag.SetTouchOnly(true);
        _subscriptions.Add(GtkSignals.Connect<Gtk.GestureDrag, Gtk.GestureDrag.DragUpdateSignalArgs>(
            h => _drag.OnDragUpdate += h, h => _drag.OnDragUpdate -= h, (gesture, args) => TouchMoved(gesture, args.OffsetX, args.OffsetY)));
        _subscriptions.Add(GtkSignals.Connect<Gtk.GestureDrag, Gtk.GestureDrag.DragEndSignalArgs>(
            h => _drag.OnDragEnd += h, h => _drag.OnDragEnd -= h, (_, _) => TouchEnded()));
        _subscriptions.Add(GtkSignals.Connect<Gtk.Gesture, Gtk.Gesture.CancelSignalArgs>(
            h => _drag.OnCancel += h, h => _drag.OnCancel -= h, (_, _) =>
            {
                _touchDecided = false;
                if (IsActive && !_closing) Cancel();
            }));
        _root.AddController(_drag);

        // Both axes: a horizontal-only controller would swallow the horizontal part of every diagonal scroll and make
        // vertical scrolling of the list stutter.
        _scroll = Gtk.EventControllerScroll.New(Gtk.EventControllerScrollFlags.BothAxes);
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerScroll>(h => _scroll.OnScrollBegin += h, h => _scroll.OnScrollBegin -= h, (_, _) =>
        {
            _touchpad = true;
            _touchpadLock = 0;
            _scrollX = 0;
            _scrollY = 0;
        }));
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerScroll, Gtk.EventControllerScroll.ScrollSignalArgs, bool>(
            h => _scroll.OnScroll += h, h => _scroll.OnScroll -= h, (controller, args) => TouchpadScrolled(controller, args.Dx, args.Dy)));
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerScroll>(h => _scroll.OnScrollEnd += h, h => _scroll.OnScrollEnd -= h, (_, _) =>
        {
            _touchpad = false;
            var horizontal = _touchpadLock == 1;
            _touchpadLock = 0;
            if (horizontal && IsActive && !_closing) Release();
        }));
        _root.AddController(_scroll);
    }

    public Gtk.Widget Root => _root;

    public Gtk.Widget Content { get; }

    /// <summary>Asked once when a swipe starts moving to a side. Null: the row does not move that way.</summary>
    public Func<WinoSwipeSide, WinoSwipeAction?>? ResolveAction { get; set; }

    /// <summary>Raised after the row slid out past the threshold and the swipe ended.</summary>
    public event Action<WinoSwipeSide>? Committed;

    public event Action? ActiveChanged;

    /// <summary>A swipe or its closing motion is in progress.</summary>
    public bool IsActive { get; private set; }

    public WinoSwipeSide Side { get; private set; }

    /// <summary>Offset over threshold; 1 or more is armed.</summary>
    public double Progress
    {
        get
        {
            var threshold = Threshold;
            return threshold <= 0 ? 0 : Math.Abs(_offset) / threshold;
        }
    }

    public bool IsArmed => _armed;

    /// <summary>The card's horizontal offset in pixels.</summary>
    public double Offset => _offset;

    private double Threshold => Math.Min(0.35 * _width, 160);

    /// <summary>Puts the row at <paramref name="progress"/> of the threshold (negative = Trailing), as a moving finger does.</summary>
    public void SetProgress(double progress)
    {
        if (_disposed || _closing) return;
        var side = progress >= 0 ? WinoSwipeSide.Leading : WinoSwipeSide.Trailing;
        if (!IsActive || Side != side)
        {
            if (!Begin(side)) return;
        }
        Move(progress * Threshold);
    }

    /// <summary>Ends the swipe as lifting the finger does: commits when armed, else slides back.</summary>
    public void Release()
    {
        if (_disposed || !IsActive || _closing) return;
        _closing = true;
        var side = Side;
        if (_armed)
        {
            Animate(side == WinoSwipeSide.Leading ? _width : -_width, CommitDuration, () =>
            {
                End();
                Committed?.Invoke(side);
            });
        }
        else Animate(0, ReturnDuration, End);
    }

    /// <summary>Puts the row back at once, without motion and without committing.</summary>
    public void Cancel()
    {
        StopMotion();
        if (IsActive) End();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopMotion();
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _root.RemoveController(_drag);
        _root.RemoveController(_scroll);
        _picture.SetPaintable(null);
        ResolveAction = null;
        Committed = null;
        ActiveChanged = null;
    }

    private void TouchMoved(Gtk.GestureDrag gesture, double dx, double dy)
    {
        if (_closing) return;
        if (!_touchDecided)
        {
            if (Math.Abs(dy) > StartDistance && Math.Abs(dy) >= Math.Abs(dx))
            {
                _touchDecided = true;
                gesture.SetState(Gtk.EventSequenceState.Denied);
                return;
            }
            if (Math.Abs(dx) <= StartDistance || Math.Abs(dx) <= 2 * Math.Abs(dy)) return;
            _touchDecided = true;
            if (!Begin(dx > 0 ? WinoSwipeSide.Leading : WinoSwipeSide.Trailing))
            {
                gesture.SetState(Gtk.EventSequenceState.Denied);
                return;
            }
            // Claimed only once the direction is known, so a vertical finger still scrolls the list.
            gesture.SetState(Gtk.EventSequenceState.Claimed);
        }
        if (IsActive) MoveTo(dx);
    }

    private void TouchEnded()
    {
        _touchDecided = false;
        if (IsActive && !_closing) Release();
    }

    private bool TouchpadScrolled(Gtk.EventControllerScroll controller, double dx, double dy)
    {
        if (!_touchpad || _closing || controller.GetUnit() != Gdk.ScrollUnit.Surface) return false;
        _scrollX += dx;
        _scrollY += dy;
        if (_touchpadLock == 0)
        {
            if (Math.Abs(_scrollY) >= TouchpadLock && Math.Abs(_scrollY) >= Math.Abs(_scrollX)) _touchpadLock = -1;
            else if (Math.Abs(_scrollX) >= TouchpadLock && Math.Abs(_scrollX) > Math.Abs(_scrollY))
            {
                _touchpadLock = Begin(-_scrollX > 0 ? WinoSwipeSide.Leading : WinoSwipeSide.Trailing) ? 1 : -1;
            }
        }
        if (_touchpadLock != 1) return false;
        // The scroll delta runs against the finger movement.
        MoveTo(-_scrollX);
        return true;
    }

    private void MoveTo(double offset)
    {
        var side = offset >= 0 ? WinoSwipeSide.Leading : WinoSwipeSide.Trailing;
        if (offset != 0 && side != Side && !Begin(side))
        {
            Move(0);
            return;
        }
        Move(offset);
    }

    private bool Begin(WinoSwipeSide side)
    {
        var width = _root.GetWidth();
        var height = _root.GetHeight();
        if (width <= 0 || height <= 0) return false;
        if (ResolveAction?.Invoke(side) is not { } action)
        {
            if (IsActive && Side != side) End();
            return false;
        }
        _width = width;
        Side = side;
        WinoIcons.SetGlyph(_glyph, WinoIcons.Glyph(action.Glyph));
        _title.SetText(action.Label);
        foreach (var tone in ToneClasses) _backdrop.RemoveCssClass(tone);
        _backdrop.AddCssClass(ToneClasses[(int)action.Tone]);
        _action.SetHalign(side == WinoSwipeSide.Leading ? Gtk.Align.Start : Gtk.Align.End);
        _card.SetSizeRequest(width, height);
        _picture.SetSizeRequest(width, height);
        _stage.Move(_card, 0, 0);
        _offset = 0;
        SetArmed(false);
        _backdrop.SetVisible(true);
        _stage.SetVisible(true);
        if (!IsActive)
        {
            IsActive = true;
            ActiveChanged?.Invoke();
        }
        return true;
    }

    private void Move(double offset)
    {
        _offset = Side == WinoSwipeSide.Leading ? Math.Clamp(offset, 0, _width) : Math.Clamp(offset, -_width, 0);
        _stage.Move(_card, _offset, 0);
        SetArmed(Math.Abs(_offset) >= Threshold && Threshold > 0);
    }

    private void SetArmed(bool armed)
    {
        if (_armed == armed) return;
        _armed = armed;
        if (armed) _backdrop.AddCssClass(WinoCssClass.SwipeArmed);
        else _backdrop.RemoveCssClass(WinoCssClass.SwipeArmed);
    }

    private void Animate(double target, uint duration, Action done)
    {
        StopMotion();
        var start = _offset;
        _clock.Restart();
        _motion = GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 16, () =>
        {
            if (_disposed)
            {
                _motion = 0;
                return false;
            }
            var t = Math.Min(1, _clock.ElapsedMilliseconds / (double)duration);
            var eased = 1 - Math.Pow(1 - t, 3);
            _offset = start + (target - start) * eased;
            _stage.Move(_card, _offset, 0);
            if (t < 1) return true;
            _motion = 0;
            done();
            return false;
        });
    }

    private void StopMotion()
    {
        if (_motion == 0) return;
        GLib.Functions.SourceRemove(_motion);
        _motion = 0;
    }

    private void End()
    {
        _closing = false;
        _backdrop.SetVisible(false);
        _stage.SetVisible(false);
        _card.SetSizeRequest(-1, -1);
        _picture.SetSizeRequest(-1, -1);
        _offset = 0;
        _stage.Move(_card, 0, 0);
        SetArmed(false);
        Side = WinoSwipeSide.None;
        if (!IsActive) return;
        IsActive = false;
        ActiveChanged?.Invoke();
    }
}
