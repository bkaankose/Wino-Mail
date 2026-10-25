using CoreGraphics;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.ToDo;

/// <summary>
/// The round task checkbox (UIKit twin of the AppKit <c>WinoRoundCheckbox</c>): an 18pt ring in the secondary colour
/// that fills with the accent and a white Checkmark glyph when on, inside a 44pt touch area. It owns no state
/// transition; the owner flips <see cref="Checked"/> after the command runs.
/// </summary>
public sealed class WinoRoundCheckbox : WinoPressableView
{
    private readonly double _size;
    private bool _checked;

    public WinoRoundCheckbox(double size = 18)
    {
        _size = size;
        Opaque = false;
        ContentMode = UIViewContentMode.Redraw;
        WinoLayout.Size(this, Math.Max(size, 28), Math.Max(size, 28));
        WinoLayout.Priorities(this, UILayoutConstraintAxis.Horizontal, 750, 750);
        AccessibilityTraits = UIAccessibilityTrait.Button;
        WinoTraits.OnAppearanceChanged(this, static view => view.SetNeedsDisplay());
        WinoStyle.AccentWatchers.Add(this, static view => view.AccentChanged(null, EventArgs.Empty));
    }

    /// <summary>Raised on a press while enabled; the owner decides what happens.</summary>
    public event EventHandler? Toggled;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            AccessibilityTraits = value ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
            SetNeedsDisplay();
        }
    }

    public string? Label { set => AccessibilityLabel = value; }

    public override CGSize IntrinsicContentSize => new(Math.Max(_size, 28), Math.Max(_size, 28));

    /// <summary>The ring takes touches across the row's 44pt height.</summary>
    public override bool PointInside(CGPoint point, UIEvent? uievent) => Bounds.Inset(-8, -8).Contains(point);

    protected override void OnEnabledChanged() { Alpha = Enabled ? 1 : 0.45f; SetNeedsDisplay(); }

    protected override void OnHoverChanged() => SetNeedsDisplay();

    protected override void OnActivated() => Toggled?.Invoke(this, EventArgs.Empty);

    private void AccentChanged(object? sender, EventArgs args) => SetNeedsDisplay();

    private CGRect CircleRect() => new(Bounds.GetMidX() - _size / 2, Bounds.GetMidY() - _size / 2, _size, _size);

    public override void Draw(CGRect rect)
    {
        var circle = CircleRect();
        if (_checked)
        {
            WinoStyle.Accent.SetFill();
            UIBezierPath.FromOval(circle).Fill();
            WinoIcons.Draw(WinoIcons.Glyph(WinoIconGlyph.Checkmark), circle.Inset((nfloat)(_size * 0.22), (nfloat)(_size * 0.22)), UIColor.White, TraitCollection, colorful: false);
            return;
        }
        var ring = UIBezierPath.FromOval(circle.Inset(0.75f, 0.75f));
        ring.LineWidth = IsHovered || IsPressed ? 2 : 1.5f;
        (IsHovered || IsPressed ? WinoStyle.Accent : WinoStyle.SecondaryText).SetStroke();
        ring.Stroke();
    }

    protected override void OnPressedChanged() => SetNeedsDisplay();

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentWatchers.Remove(this);
        base.Dispose(disposing);
    }
}
