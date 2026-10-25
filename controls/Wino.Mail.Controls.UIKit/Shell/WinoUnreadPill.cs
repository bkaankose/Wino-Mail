using CoreAnimation;
using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Mail.Controls.Core;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Shell;

/// <summary>
/// The accent unread pill of the Windows InfoBadge (UIKit twin of the AppKit <c>WinoUnreadPill</c>): 18pt tall, radius
/// 9, white 11pt semibold count, minimum width 18 with 5pt side padding. Hidden while the count is zero.
/// </summary>
public sealed class WinoUnreadPill : WinoSurfaceView
{
    private readonly UILabel _label;

    public WinoUnreadPill()
    {
        CornerRadius = 9;
        Fill = WinoStyle.Accent;
        _label = WinoStyle.Label(string.Empty, WinoStyle.Fixed(11, UIFontWeight.Semibold), UIColor.White);
        _label.TextAlignment = UITextAlignment.Center;
        _label.AdjustsFontForContentSizeCategory = false;
        AddSubview(_label);
        WinoLayout.Priorities(_label, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        NSLayoutConstraint.ActivateConstraints(
        [
            _label.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _label.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            HeightAnchor.ConstraintEqualTo(18),
            WidthAnchor.ConstraintGreaterThanOrEqualTo(18),
            WidthAnchor.ConstraintGreaterThanOrEqualTo(_label.WidthAnchor, 1, 10)
        ]);
        WinoLayout.Priorities(this, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        SetContentHuggingPriority(1000, UILayoutConstraintAxis.Vertical);
        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.StaticText;
        WinoStyle.AccentWatchers.Add(this, static view => view.AccentChanged(null, EventArgs.Empty));
        Hidden = true;
    }

    private void AccentChanged(object? sender, EventArgs args) => Fill = WinoStyle.Accent;

    public override CGSize IntrinsicContentSize
    {
        get
        {
            var label = _label.IntrinsicContentSize;
            return new CGSize(Math.Max(18, Math.Ceiling(label.Width) + 10), 18);
        }
    }

    public int Count
    {
        set
        {
            var text = WinoControlText.UnreadPill(value);
            if (_label.Text != text)
            {
                _label.Text = text;
                InvalidateIntrinsicContentSize();
            }
            AccessibilityLabel = text;
            Hidden = value <= 0;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentWatchers.Remove(this);
        base.Dispose(disposing);
    }
}

/// <summary>
/// The 3pt synchronization bar under an account name (Windows ProgressBar Height=3; UIKit twin of the AppKit
/// <c>WinoSyncBar</c>). Determinate fills from the leading edge; indeterminate slides a third-width segment across.
/// </summary>
public sealed class WinoSyncBar : UIView
{
    private const string AnimationKey = "wino.sync";
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
        ClipsToBounds = true;
        WinoLayout.Fill(_track, this);
        AddSubview(_fill);
        NSLayoutConstraint.ActivateConstraints(
        [
            HeightAnchor.ConstraintEqualTo(3),
            _fill.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _fill.TopAnchor.ConstraintEqualTo(TopAnchor),
            _fill.BottomAnchor.ConstraintEqualTo(BottomAnchor)
        ]);
        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.UpdatesFrequently;
        Update(false, 0);
        WinoStyle.AccentWatchers.Add(this, static view => view.AccentChanged(null, EventArgs.Empty));
    }

    private void AccentChanged(object? sender, EventArgs args) => _fill.Fill = WinoStyle.Accent;

    /// <summary>Sets the bar; <paramref name="value"/> is 0 to 100.</summary>
    public void Update(bool indeterminate, double value)
    {
        _indeterminate = indeterminate;
        var fraction = indeterminate ? 0.34 : Math.Clamp(value / 100, 0, 1);
        if (fraction != _fraction)
        {
            _fraction = fraction;
            if (_fillWidth is not null) _fillWidth.Active = false;
            // A zero multiplier is rejected by Auto Layout; use a hair-thin fill instead.
            _fillWidth = _fill.WidthAnchor.ConstraintEqualTo(WidthAnchor, (nfloat)Math.Max(fraction, 0.001));
            _fillWidth.Active = true;
        }
        AccessibilityValue = indeterminate ? null : $"{Math.Round(fraction * 100)}%";
        UpdateAnimation();
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        UpdateAnimation();
    }

    public override bool Hidden
    {
        get => base.Hidden;
        set { base.Hidden = value; UpdateAnimation(); }
    }

    public override void MovedToWindow()
    {
        base.MovedToWindow();
        // Core Animation drops animations when a layer leaves the window; attach again on return.
        _animatedWidth = -1;
        UpdateAnimation();
    }

    /// <summary>Attaches the slide when indeterminate turns on or the width changes; detaches it when it turns off.</summary>
    private void UpdateAnimation()
    {
        var layer = _fill.Layer;
        bool animate = _indeterminate && !Hidden && Window is not null && Bounds.Width > 0;
        if (!animate)
        {
            if (_animatedWidth < 0) return;
            layer.RemoveAnimation(AnimationKey);
            _animatedWidth = -1;
            return;
        }
        if (_animatedWidth == Bounds.Width && layer.AnimationForKey(AnimationKey) is not null) return;
        _animatedWidth = Bounds.Width;
        var animation = CABasicAnimation.FromKeyPath("transform.translation.x");
        animation.From = NSNumber.FromDouble(-Bounds.Width * 0.34);
        animation.To = NSNumber.FromDouble(Bounds.Width);
        animation.Duration = 1.4;
        animation.RepeatCount = float.MaxValue;
        layer.AddAnimation(animation, AnimationKey);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentWatchers.Remove(this);
        base.Dispose(disposing);
    }
}
