using AppKit;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Shell;

/// <summary>
/// The accent unread pill of the Windows InfoBadge: 18pt tall, radius 9, white 11pt semibold
/// count, min width 18 with 5pt side padding. Hidden while the count is zero.
/// </summary>
public sealed class WinoUnreadPill : WinoSurfaceView
{
    private readonly NSTextField _label;

    public WinoUnreadPill()
    {
        CornerRadius = 9;
        Fill = WinoStyle.Accent;
        _label = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(11, NSFontWeight.Semibold), NSColor.White);
        _label.Alignment = NSTextAlignment.Center;
        AddSubview(_label);
        _label.SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Horizontal);
        _label.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);
        NSLayoutConstraint.ActivateConstraints(
        [
            _label.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _label.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            HeightAnchor.ConstraintEqualTo(18),
            WidthAnchor.ConstraintGreaterThanOrEqualTo(18),
            WidthAnchor.ConstraintGreaterThanOrEqualTo(_label.WidthAnchor, 1, 10)
        ]);
        // A plain NSView has no intrinsic size, so hugging alone never stopped the row's stack view
        // from stretching the pill. The intrinsic width (label + padding) lets it hug its count.
        SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Horizontal);
        SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Vertical);
        SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);
        WinoStyle.AccentChanged += AccentChanged;
        Hidden = true;
    }

    private void AccentChanged(object? sender, EventArgs args) => Fill = WinoStyle.Accent;

    public override CoreGraphics.CGSize IntrinsicContentSize
    {
        get
        {
            var label = _label.IntrinsicContentSize;
            return new CoreGraphics.CGSize(Math.Max(18, Math.Ceiling(label.Width) + 10), 18);
        }
    }

    public int Count
    {
        set
        {
            var text = value > 999 ? "999+" : value.ToString();
            if (_label.StringValue != text)
            {
                _label.StringValue = text;
                InvalidateIntrinsicContentSize();
            }
            AccessibilityLabel = text;
            Hidden = value <= 0;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentChanged -= AccentChanged;
        base.Dispose(disposing);
    }
}

/// <summary>
/// The 3pt synchronization bar under an account name (Windows ProgressBar Height=3). Determinate
/// fills from the left; indeterminate slides a third-width segment across the track.
/// </summary>
public sealed class WinoSyncBar : NSView
{
    private readonly WinoSurfaceView _track = new() { CornerRadius = 1.5, Fill = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.08), WinoStyle.Hex(0xFFFFFF, 0.12)) };
    private readonly WinoSurfaceView _fill = new() { CornerRadius = 1.5, Fill = WinoStyle.Accent };
    private NSLayoutConstraint? _fillWidth;
    private double _fraction = -1;
    private bool _indeterminate;
    // Width the running animation was built for; -1 while no animation is attached.
    private double _animatedWidth = -1;

    public WinoSyncBar()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        WinoLayout.Fill(_track, this);
        AddSubview(_fill);
        NSLayoutConstraint.ActivateConstraints(
        [
            HeightAnchor.ConstraintEqualTo(3),
            _fill.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _fill.TopAnchor.ConstraintEqualTo(TopAnchor),
            _fill.BottomAnchor.ConstraintEqualTo(BottomAnchor)
        ]);
        Update(false, 0);
        WinoStyle.AccentChanged += AccentChanged;
    }

    private void AccentChanged(object? sender, EventArgs args) => _fill.Fill = WinoStyle.Accent;

    /// <summary>Sets the bar; <paramref name="value"/> is 0 to 100.</summary>
    public void Update(bool indeterminate, double value)
    {
        _indeterminate = indeterminate;
        var fraction = indeterminate ? 0.34 : Math.Clamp(value / 100, 0, 1);
        // The multiplier is read-only, so the constraint is replaced, but only when the fill really moves.
        if (fraction != _fraction)
        {
            _fraction = fraction;
            if (_fillWidth is not null) _fillWidth.Active = false;
            // A zero multiplier is rejected by Auto Layout; use a hair-thin fill instead.
            _fillWidth = _fill.WidthAnchor.ConstraintEqualTo(WidthAnchor, (nfloat)Math.Max(fraction, 0.001), 0);
            _fillWidth.Active = true;
        }
        UpdateAnimation();
    }

    public override void Layout()
    {
        base.Layout();
        UpdateAnimation();
    }

    public override void ViewDidHide()
    {
        base.ViewDidHide();
        UpdateAnimation();
    }

    public override void ViewDidUnhide()
    {
        base.ViewDidUnhide();
        UpdateAnimation();
    }

    /// <summary>Attaches the slide when indeterminate turns on or the width changes; detaches it when it turns off.</summary>
    private void UpdateAnimation()
    {
        if (_fill.Layer is not { } layer) return;
        bool animate = _indeterminate && !Hidden && Bounds.Width > 0;
        if (!animate)
        {
            if (_animatedWidth < 0) return;
            layer.RemoveAnimation("wino.sync");
            _animatedWidth = -1;
            return;
        }
        if (_animatedWidth == Bounds.Width && layer.AnimationForKey("wino.sync") is not null) return;
        _animatedWidth = Bounds.Width;
        var animation = CoreAnimation.CABasicAnimation.FromKeyPath("transform.translation.x");
        animation.From = Foundation.NSNumber.FromDouble(-Bounds.Width * 0.34);
        animation.To = Foundation.NSNumber.FromDouble(Bounds.Width);
        animation.Duration = 1.4;
        animation.RepeatCount = float.MaxValue;
        layer.AddAnimation(animation, "wino.sync");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentChanged -= AccentChanged;
        base.Dispose(disposing);
    }
}
