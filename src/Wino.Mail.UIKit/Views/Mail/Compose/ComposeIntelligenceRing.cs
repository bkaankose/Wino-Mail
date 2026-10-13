using CoreAnimation;
using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail.Compose;

/// <summary>
/// Busy indicator of the rewrite strip (Mac ComposeIntelligenceRing, Windows WinoIntelligenceProgressRing): a 22pt
/// ring stroked with the Wino Intelligence brand gradient, rotating while <see cref="IsAnimating"/>. Reduce Motion
/// keeps it still.
/// </summary>
internal sealed class ComposeIntelligenceRing : UIView
{
    private const double Diameter = 22;
    private const string SpinKey = "wino.spin";
    private readonly CALayer _spinner = new();
    private readonly CAGradientLayer _gradient = new() { LayerType = CAGradientLayerType.Conic };
    private readonly CAShapeLayer _mask = new();
    private bool _animating;

    public ComposeIntelligenceRing()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WinoLayout.Size(this, Diameter, Diameter);
        _gradient.StartPoint = new CGPoint(0.5, 0.5);
        _gradient.EndPoint = new CGPoint(1, 0.5);
        _mask.FillColor = null;
        _mask.StrokeColor = UIColor.Black.CGColor;
        _mask.LineWidth = 2.5f;
        _mask.LineCap = CAShapeLayer.CapRound;
        _mask.StrokeStart = 0.08f;
        _mask.StrokeEnd = 1;
        _gradient.Mask = _mask;
        _spinner.AddSublayer(_gradient);
        Layer.AddSublayer(_spinner);
        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.UpdatesFrequently;
        WinoTraits.OnAppearanceChanged(this, static ring => ring.UpdateColors());
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

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        CATransaction.Begin();
        CATransaction.DisableActions = true;
        _spinner.Frame = Bounds;
        _gradient.Frame = Bounds;
        _mask.Frame = Bounds;
        var inset = Bounds.Inset(2, 2);
        _mask.Path = UIBezierPath.FromOval(inset).CGPath;
        CATransaction.Commit();
    }

    public override void MovedToWindow()
    {
        base.MovedToWindow();
        UpdateAnimation();
    }

    private void UpdateColors()
        => _gradient.Colors = ComposeBrand.Colors(WinoTraits.IsDark(TraitCollection)).Select(static color => color.CGColor).ToArray();

    private void UpdateAnimation()
    {
        _spinner.RemoveAnimation(SpinKey);
        if (!_animating || Window is null || UIAccessibility.IsReduceMotionEnabled) return;
        var spin = CABasicAnimation.FromKeyPath("transform.rotation.z");
        spin.From = NSNumber.FromDouble(0);
        spin.To = NSNumber.FromDouble(Math.PI * 2);
        spin.Duration = 1.1;
        spin.RepeatCount = float.PositiveInfinity;
        spin.RemovedOnCompletion = false;
        _spinner.AddAnimation(spin, SpinKey);
    }
}

/// <summary>The Wino Intelligence brand gradient (local twin of the Mac WinoIntelligenceBrand) for the composer.</summary>
internal static class ComposeBrand
{
    private static readonly nfloat[] Locations = [0, 0.42f, 0.66f, 1];

    public static UIColor[] Colors(bool dark) => dark
        ? [WinoStyle.Hex(0x5FA8FF), WinoStyle.Hex(0x72D1FF), WinoStyle.Hex(0xB59BFF), WinoStyle.Hex(0x3298F4)]
        : [WinoStyle.Hex(0x075FCC), WinoStyle.Hex(0x58BFFF), WinoStyle.Hex(0x7C5CFC), WinoStyle.Hex(0x1685EA)];

    /// <summary>A Wino glyph filled with the brand gradient, with a light and a dark variant.</summary>
    public static UIImage GlyphImage(WinoIconGlyph glyph, double size, string? accessibilityDescription = null)
    {
        var light = UITraitCollection.FromUserInterfaceStyle(UIUserInterfaceStyle.Light);
        var dark = UITraitCollection.FromUserInterfaceStyle(UIUserInterfaceStyle.Dark);
        var asset = new UIImageAsset();
        asset.RegisterImage(Render(glyph, size, light, false), light);
        asset.RegisterImage(Render(glyph, size, dark, true), dark);
        var image = asset.FromTraitCollection(UITraitCollection.CurrentTraitCollection);
        image.AccessibilityLabel = accessibilityDescription;
        return image;
    }

    private static UIImage Render(WinoIconGlyph glyph, double size, UITraitCollection traits, bool dark)
    {
        var text = WinoIcons.Glyph(glyph);
        using var renderer = new UIGraphicsImageRenderer(new CGSize(size, size));
        var image = renderer.CreateImage(context =>
        {
            var rect = new CGRect(0, 0, size, size);
            var cg = context.CGContext;
            using var space = CGColorSpace.CreateSrgb();
            if (space is null)
            {
                // No sRGB space: the flat brand colour instead of the gradient.
                WinoIcons.Draw(text, rect, Colors(dark)[0], traits, false);
                return;
            }
            cg.BeginTransparencyLayer();
            WinoIcons.Draw(text, rect, UIColor.Black, traits, false);
            cg.SetBlendMode(CGBlendMode.SourceIn);
            using var gradient = new CGGradient(space, Colors(dark).Select(static color => color.CGColor).ToArray(), Locations);
            cg.DrawLinearGradient(gradient, new CGPoint(rect.Left, rect.Top), new CGPoint(rect.Right, rect.Bottom),
                CGGradientDrawingOptions.DrawsBeforeStartLocation | CGGradientDrawingOptions.DrawsAfterEndLocation);
            cg.EndTransparencyLayer();
        });
        return image.ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
    }
}
