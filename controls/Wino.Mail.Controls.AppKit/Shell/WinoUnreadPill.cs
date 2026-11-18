using AppKit;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using Wino.Mail.Controls.Core;
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
        Fill = BadgeFill;
        _label = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(11, NSFontWeight.Semibold), WinoStyle.SecondaryText);
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
        Hidden = true;
    }

    /// <summary>The grey source-list badge (Mail, Finder), not the accent, so no accent subscription is needed.</summary>
    private static NSColor BadgeFill { get; } = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.08), WinoStyle.Hex(0xFFFFFF, 0.14));

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
            var text = WinoControlText.UnreadPill(value);
            if (_label.StringValue != text)
            {
                _label.StringValue = text;
                InvalidateIntrinsicContentSize();
            }
            AccessibilityLabel = text;
            Hidden = value <= 0;
        }
    }

}

/// <summary>
/// The 3pt synchronization bar under an account name (Windows ProgressBar Height=3), drawn with layers
/// clipped to a rounded track. Determinate progress eases to each new value; indeterminate sweeps a
/// segment that grows and shrinks across the track, and the bar fades in when it appears.
/// </summary>
public sealed class WinoSyncBar : NSView
{
    private const string SweepKey = "wino.sync.sweep";
    private const double Height = 3;

    private static readonly NSColor TrackColor = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.08), WinoStyle.Hex(0xFFFFFF, 0.12));

    private readonly CALayer _fill = new() { CornerRadius = (nfloat)(Height / 2), AnchorPoint = new CGPoint(0, 0.5) };
    private double _fraction;
    private bool _indeterminate;
    private bool _hasValue;
    // Width the running sweep was built for; -1 while no sweep is attached.
    private double _sweepWidth = -1;

    public WinoSyncBar()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        HeightAnchor.ConstraintEqualTo((nfloat)Height).Active = true;
        WinoStyle.AccentWatchers.Add(this, static bar => bar.NeedsDisplay = true);
    }

    public override bool WantsUpdateLayer => true;

    public override void UpdateLayer()
    {
        if (Layer is not { } layer) return;
        layer.MasksToBounds = true;
        layer.CornerRadius = (nfloat)(Height / 2);
        layer.BackgroundColor = TrackColor.CGColor;
        if (_fill.SuperLayer is null) layer.AddSublayer(_fill);
        WithoutActions(() => _fill.BackgroundColor = WinoStyle.Accent.CGColor);
    }

    public void ForgetProgress() => _hasValue = false;

    /// <summary>Sets the bar; <paramref name="value"/> is 0 to 100.</summary>
    public void Update(bool indeterminate, double value)
    {
        var fraction = Math.Clamp(value / 100, 0, 1);
        var modeChanged = indeterminate != _indeterminate || !_hasValue;
        var animate = _hasValue && !modeChanged && !indeterminate && fraction > _fraction && Window is not null && !Hidden;
        _indeterminate = indeterminate;
        _fraction = fraction;
        _hasValue = true;
        ApplyFill(animate);
    }

    public override void Layout()
    {
        base.Layout();
        ApplyFill(animate: false);
    }

    public override void ViewDidHide()
    {
        base.ViewDidHide();
        ApplySweep();
    }

    public override void ViewDidUnhide()
    {
        base.ViewDidUnhide();
        ApplyFill(animate: false);
        if (Layer is not { } layer) return;
        var fade = CABasicAnimation.FromKeyPath("opacity");
        fade.From = NSNumber.FromDouble(0);
        fade.To = NSNumber.FromDouble(1);
        fade.Duration = 0.2;
        layer.AddAnimation(fade, "wino.sync.fade");
    }

    private void ApplyFill(bool animate)
    {
        var width = Bounds.Width;
        var segment = _indeterminate ? width * 0.3 : width * _fraction;
        // A layer added to a view-backed layer animates every frame change by default; only progress steps should.
        if (animate)
        {
            CATransaction.Begin();
            CATransaction.AnimationDuration = 0.35;
            CATransaction.AnimationTimingFunction = CAMediaTimingFunction.FromName(CAMediaTimingFunction.EaseOut);
            _fill.Bounds = new CGRect(0, 0, segment, Height);
            _fill.Position = new CGPoint(0, Bounds.Height / 2);
            CATransaction.Commit();
        }
        else
        {
            WithoutActions(() =>
            {
                _fill.Bounds = new CGRect(0, 0, segment, Height);
                _fill.Position = new CGPoint(0, Bounds.Height / 2);
            });
        }
        ApplySweep();
    }

    /// <summary>Attaches the sweep while indeterminate and visible; rebuilds it when the width changes.</summary>
    private void ApplySweep()
    {
        var width = Bounds.Width;
        if (!_indeterminate || Hidden || width <= 0)
        {
            if (_sweepWidth < 0) return;
            _fill.RemoveAnimation(SweepKey);
            _sweepWidth = -1;
            return;
        }
        if (_sweepWidth == width && _fill.AnimationForKey(SweepKey) is not null) return;
        _sweepWidth = width;

        // The segment enters from the left edge and leaves past the right one, widening in the middle of
        // the track like the system's indeterminate bar; the track clips it.
        var ease = CAMediaTimingFunction.FromName(CAMediaTimingFunction.EaseInEaseOut);
        var move = CAKeyFrameAnimation.FromKeyPath("position.x");
        move.Values = [NSNumber.FromDouble(-width * 0.3), NSNumber.FromDouble(width * 0.35), NSNumber.FromDouble(width)];
        move.KeyTimes = [NSNumber.FromDouble(0), NSNumber.FromDouble(0.5), NSNumber.FromDouble(1)];
        move.TimingFunctions = [ease, ease];
        var grow = CAKeyFrameAnimation.FromKeyPath("bounds.size.width");
        grow.Values = [NSNumber.FromDouble(width * 0.2), NSNumber.FromDouble(width * 0.45), NSNumber.FromDouble(width * 0.2)];
        grow.KeyTimes = [NSNumber.FromDouble(0), NSNumber.FromDouble(0.5), NSNumber.FromDouble(1)];
        grow.TimingFunctions = [ease, ease];
        var sweep = CAAnimationGroup.CreateAnimation();
        sweep.Animations = [move, grow];
        sweep.Duration = 1.5;
        sweep.RepeatCount = float.MaxValue;
        _fill.AddAnimation(sweep, SweepKey);
    }

    private static void WithoutActions(Action change)
    {
        CATransaction.Begin();
        CATransaction.DisableActions = true;
        change();
        CATransaction.Commit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentWatchers.Remove(this);
        base.Dispose(disposing);
    }
}
