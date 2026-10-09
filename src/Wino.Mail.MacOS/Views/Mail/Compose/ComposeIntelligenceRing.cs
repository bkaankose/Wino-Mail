using AppKit;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using Wino.Mail.MacOS.Views.Settings;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail.Compose;

/// <summary>
/// Busy indicator of the rewrite strip: a 22pt ring stroked with the Wino Intelligence brand
/// gradient, rotating while <see cref="IsAnimating"/> (Windows WinoIntelligenceProgressRing).
/// Reduce Motion keeps the ring still.
/// </summary>
internal sealed class ComposeIntelligenceRing : NSView
{
    private const double Diameter = 22;
    private readonly CALayer _spinner = new();
    private readonly CAGradientLayer _gradient = new() { LayerType = CAGradientLayerType.Conic };
    private readonly CAShapeLayer _mask = new();
    private bool _animating;

    public ComposeIntelligenceRing()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        WinoLayout.Size(this, Diameter, Diameter);
        _gradient.StartPoint = new CGPoint(0.5, 0.5);
        _gradient.EndPoint = new CGPoint(1, 0.5);
        _mask.FillColor = null;
        _mask.StrokeColor = NSColor.Black.CGColor;
        _mask.LineWidth = 2.5f;
        _mask.LineCap = CAShapeLayer.CapRound;
        _mask.StrokeStart = 0.08f;
        _mask.StrokeEnd = 1;
        _gradient.Mask = _mask;
        _spinner.AddSublayer(_gradient);
        Layer!.AddSublayer(_spinner);
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.BusyIndicatorRole;
        UpdateColors();
    }

    public override CGSize IntrinsicContentSize => new(Diameter, Diameter);

    public bool IsAnimating
    {
        get => _animating;
        set
        {
            if (_animating == value) return;
            _animating = value;
            UpdateAnimation();
        }
    }

    public override void Layout()
    {
        base.Layout();
        CATransaction.Begin();
        CATransaction.DisableActions = true;
        var bounds = Bounds;
        _spinner.Bounds = bounds;
        _spinner.AnchorPoint = new CGPoint(0.5, 0.5);
        _spinner.Position = new CGPoint(bounds.GetMidX(), bounds.GetMidY());
        _gradient.Frame = bounds;
        _mask.Frame = bounds;
        var inset = _mask.LineWidth / 2 + 1;
        _mask.Path = CGPath.EllipseFromRect(bounds.Inset(inset, inset));
        CATransaction.Commit();
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        UpdateColors();
    }

    public override void ViewDidMoveToWindow()
    {
        base.ViewDidMoveToWindow();
        UpdateAnimation();
    }

    private void UpdateColors()
    {
        var dark = WinoIcons.IsDark(EffectiveAppearance);
        var colors = dark
            ? new[] { WinoStyle.Hex(0x5FA8FF), WinoStyle.Hex(0x72D1FF), WinoStyle.Hex(0xB59BFF), WinoStyle.Hex(0x3298F4) }
            : new[] { WinoStyle.Hex(0x075FCC), WinoStyle.Hex(0x58BFFF), WinoStyle.Hex(0x7C5CFC), WinoStyle.Hex(0x1685EA) };
        _gradient.Colors = colors.Select(color => color.CGColor).ToArray();
    }

    private void UpdateAnimation()
    {
        _spinner.RemoveAnimation("spin");
        if (!_animating || Window is null || NSWorkspace.SharedWorkspace.AccessibilityDisplayShouldReduceMotion) return;
        var spin = CABasicAnimation.FromKeyPath("transform.rotation.z");
        spin.From = NSNumber.FromDouble(0);
        spin.To = NSNumber.FromDouble(-Math.PI * 2);
        spin.Duration = 1.1;
        spin.RepeatCount = float.PositiveInfinity;
        spin.RemovedOnCompletion = false;
        _spinner.AddAnimation(spin, "spin");
    }
}
