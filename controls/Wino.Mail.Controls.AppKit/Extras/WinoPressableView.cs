using AppKit;
using CoreGraphics;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Extras;

/// <summary>
/// A surface that behaves like a button: standard tracking (pressed while the mouse is down and
/// inside, <see cref="Clicked"/> on a mouse-up inside), Space/Return and the system focus ring
/// when keyboard navigation reaches it, and VoiceOver's press action. Used for the Wino-styled
/// pills, chips and rows that AppKit's bezels cannot draw.
/// </summary>
public class WinoPressableView : WinoSurfaceView
{
    private bool _enabled = true;
    private bool _tracking;
    private bool _pressed;

    public WinoPressableView()
    {
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.ButtonRole;
    }

    public event EventHandler? Clicked;

    public object? Tag { get; set; }

    /// <summary>A disabled view ignores mouse, keyboard and VoiceOver presses.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            _tracking = false;
            SetPressed(false);
            AccessibilityEnabled = value;
            OnEnabledChanged();
        }
    }

    /// <summary>True while the mouse is down and inside the bounds.</summary>
    protected bool IsPressed => _pressed;

    protected virtual void OnEnabledChanged() { }

    /// <summary>The default pressed look dims the whole view.</summary>
    protected virtual void OnPressedChanged() => AlphaValue = _pressed ? (nfloat)0.8 : 1;

    /// <summary>Runs the press. Subclasses that pop a menu or raise their own event override this.</summary>
    protected virtual void OnActivated() => Clicked?.Invoke(this, EventArgs.Empty);

    /// <summary>Menu-carrying views open on mouse-down like NSPopUpButton; the menu then tracks the mouse itself.</summary>
    protected virtual bool ActivatesOnMouseDown => false;

    protected virtual double FocusRingCornerRadius => CornerRadius;

    private void SetPressed(bool pressed)
    {
        if (_pressed == pressed) return;
        _pressed = pressed;
        OnPressedChanged();
    }

    private bool IsInside(NSEvent theEvent) => Bounds.Contains(ConvertPointFromView(theEvent.LocationInWindow, null));

    public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

    // Like NSButton: focusable only while enabled and with the system "Keyboard navigation" setting on.
    public override bool AcceptsFirstResponder() => _enabled && NSApplication.SharedApplication.FullKeyboardAccessEnabled;

    public override void MouseDown(NSEvent theEvent)
    {
        if (!_enabled) { base.MouseDown(theEvent); return; }
        if (ActivatesOnMouseDown) { OnActivated(); return; }
        _tracking = true;
        SetPressed(true);
    }

    public override void MouseDragged(NSEvent theEvent)
    {
        if (!_tracking) { base.MouseDragged(theEvent); return; }
        SetPressed(IsInside(theEvent));
    }

    public override void MouseUp(NSEvent theEvent)
    {
        if (!_tracking) { base.MouseUp(theEvent); return; }
        _tracking = false;
        SetPressed(false);
        if (IsInside(theEvent)) OnActivated();
    }

    public override void KeyDown(NSEvent theEvent)
    {
        if (_enabled && !theEvent.IsARepeat && theEvent.CharactersIgnoringModifiers is " " or "\r" or "\u0003") { OnActivated(); return; }
        base.KeyDown(theEvent);
    }

    public override CGRect FocusRingMaskBounds => Bounds;

    public override void DrawFocusRingMask()
    {
        var radius = (nfloat)FocusRingCornerRadius;
        NSBezierPath.FromRoundedRect(Bounds, radius, radius).Fill();
    }

    public override bool AccessibilityPerformPress()
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
