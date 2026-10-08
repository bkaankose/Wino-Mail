using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Extras;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.ToDo;

/// <summary>
/// The round task checkbox from the design board: an 18pt ring in the secondary colour that
/// fills with the accent and a white Checkmark glyph when on. Hover thickens the ring. It owns no
/// state transition; the owner flips <see cref="Checked"/> after the command runs. Press tracking,
/// keyboard and the focus ring come from <see cref="WinoPressableView"/>.
/// </summary>
public sealed class WinoRoundCheckbox : WinoPressableView
{
    private readonly double _size;
    private NSTrackingArea? _tracking;
    private bool _checked;
    private bool _hovered;

    public WinoRoundCheckbox(double size = 18)
    {
        _size = size;
        WinoLayout.Size(this, size, size);
        SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        AccessibilityRole = NSAccessibilityRoles.CheckBoxRole;
        AccessibilityValue = new NSNumber(0);
    }

    /// <summary>Raised on a press while enabled; the owner decides what happens.</summary>
    public event EventHandler? Toggled;

    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; AccessibilityValue = new NSNumber(value ? 1 : 0); NeedsDisplay = true; }
    }

    public string? Label { set => AccessibilityLabel = value; }

    // Drawn in DrawRect (not the surface layer) in unflipped coordinates, as before the pressable base.
    public override bool WantsUpdateLayer => false;
    public override bool IsFlipped => false;

    public override CGSize IntrinsicContentSize => new((nfloat)_size, (nfloat)_size);

    protected override void OnEnabledChanged() { AlphaValue = Enabled ? 1 : (nfloat)0.45; NeedsDisplay = true; }

    protected override void OnActivated() => Toggled?.Invoke(this, EventArgs.Empty);

    public override void DrawFocusRingMask() => NSBezierPath.FromOvalInRect(CircleRect()).Fill();

    public override void UpdateTrackingAreas()
    {
        base.UpdateTrackingAreas();
        if (_tracking is not null) RemoveTrackingArea(_tracking);
        _tracking = new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow | NSTrackingAreaOptions.InVisibleRect, this, null);
        AddTrackingArea(_tracking);
    }

    public override void MouseEntered(NSEvent theEvent) { _hovered = true; NeedsDisplay = true; }
    public override void MouseExited(NSEvent theEvent) { _hovered = false; NeedsDisplay = true; }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        NeedsDisplay = true;
    }

    private CGRect CircleRect() => new(Bounds.GetMidX() - _size / 2, Bounds.GetMidY() - _size / 2, _size, _size);

    public override void DrawRect(CGRect dirtyRect)
    {
        var rect = CircleRect();
        if (_checked)
        {
            WinoStyle.Accent.SetFill();
            NSBezierPath.FromOvalInRect(rect).Fill();
            WinoIcons.Draw(WinoIcons.Glyph(WinoIconGlyph.Checkmark), rect.Inset((nfloat)(_size * 0.22), (nfloat)(_size * 0.22)), NSColor.White, EffectiveAppearance, colorful: false);
            return;
        }
        var ring = NSBezierPath.FromOvalInRect(rect.Inset((nfloat)0.75, (nfloat)0.75));
        ring.LineWidth = _hovered ? 2 : (nfloat)1.5;
        (_hovered ? WinoStyle.Accent : WinoStyle.SecondaryText).SetStroke();
        ring.Stroke();
    }
}
