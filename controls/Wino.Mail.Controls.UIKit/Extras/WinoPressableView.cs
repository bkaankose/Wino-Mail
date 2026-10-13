using Foundation;
using UIKit;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Extras;

/// <summary>
/// A surface that behaves like a button (UIKit twin of the AppKit <c>WinoPressableView</c>): touch tracking (pressed
/// while the finger is down and inside, <see cref="Clicked"/> on a lift inside), the pointer hover state on iPad, and
/// VoiceOver's activate. Used for the Wino-styled pills, chips and tiles that system buttons cannot draw.
/// </summary>
public class WinoPressableView : WinoSurfaceView
{
    private bool _enabled = true;
    private bool _tracking;
    private bool _pressed;
    private bool _hovered;

    public WinoPressableView()
    {
        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.Button;
        AddGestureRecognizer(new UIHoverGestureRecognizer(HoverChanged));
    }

    public event EventHandler? Clicked;

    public new object? Tag { get; set; }

    /// <summary>A disabled view ignores touches and VoiceOver activation.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            _tracking = false;
            SetPressed(false);
            AccessibilityTraits = value ? AccessibilityTraits & ~UIAccessibilityTrait.NotEnabled : AccessibilityTraits | UIAccessibilityTrait.NotEnabled;
            OnEnabledChanged();
        }
    }

    /// <summary>True while a touch is down and inside the bounds.</summary>
    protected bool IsPressed => _pressed;

    /// <summary>True while the iPad pointer is over the view.</summary>
    protected bool IsHovered => _hovered;

    protected virtual void OnEnabledChanged() { }

    protected virtual void OnHoverChanged() { }

    /// <summary>The default pressed look dims the whole view.</summary>
    protected virtual void OnPressedChanged() => Alpha = _pressed ? (nfloat)0.6 : 1;

    /// <summary>Runs the press. Subclasses that raise their own event override this.</summary>
    protected virtual void OnActivated() => Clicked?.Invoke(this, EventArgs.Empty);

    private void HoverChanged(UIHoverGestureRecognizer recognizer)
    {
        bool hovered = _enabled && recognizer.State is UIGestureRecognizerState.Began or UIGestureRecognizerState.Changed;
        if (_hovered == hovered) return;
        _hovered = hovered;
        OnHoverChanged();
    }

    private void SetPressed(bool pressed)
    {
        if (_pressed == pressed) return;
        _pressed = pressed;
        OnPressedChanged();
    }

    private bool IsInside(NSSet touches)
        => touches.AnyObject is UITouch touch && Bounds.Inset(-12, -12).Contains(touch.LocationInView(this));

    public override void TouchesBegan(NSSet touches, UIEvent? evt)
    {
        if (!_enabled) { base.TouchesBegan(touches, evt); return; }
        _tracking = true;
        SetPressed(true);
    }

    public override void TouchesMoved(NSSet touches, UIEvent? evt)
    {
        if (!_tracking) { base.TouchesMoved(touches, evt); return; }
        SetPressed(IsInside(touches));
    }

    public override void TouchesEnded(NSSet touches, UIEvent? evt)
    {
        if (!_tracking) { base.TouchesEnded(touches, evt); return; }
        _tracking = false;
        SetPressed(false);
        if (IsInside(touches)) OnActivated();
    }

    public override void TouchesCancelled(NSSet touches, UIEvent? evt)
    {
        _tracking = false;
        SetPressed(false);
        base.TouchesCancelled(touches, evt);
    }

    public override bool AccessibilityActivate()
    {
        if (!_enabled) return false;
        OnActivated();
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Clicked = null;
        base.Dispose(disposing);
    }
}
