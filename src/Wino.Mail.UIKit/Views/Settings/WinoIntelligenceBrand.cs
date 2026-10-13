using CoreGraphics;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Wino Intelligence branding: the Windows WinoIntelligenceBrandIconBrush gradient (0,0 to 1,1)
/// for glyphs, the flat brand colour for small shapes, and the promotion illustration.
/// </summary>
public static class WinoIntelligenceBrand
{
    private static readonly nfloat[] Locations = [0, 0.42f, 0.66f, 1];

    /// <summary>Flat brand colour (WinoIllustrationBrandSolidBrush).</summary>
    public static UIColor Solid => WinoStyle.Dynamic(WinoStyle.Hex(0x3B84E8), WinoStyle.Hex(0x7FBAFF));

    public static CGGradient Gradient(bool dark)
    {
        var colors = dark
            ? new[] { WinoStyle.Hex(0x5FA8FF), WinoStyle.Hex(0x72D1FF), WinoStyle.Hex(0xB59BFF), WinoStyle.Hex(0x3298F4) }
            : new[] { WinoStyle.Hex(0x075FCC), WinoStyle.Hex(0x58BFFF), WinoStyle.Hex(0x7C5CFC), WinoStyle.Hex(0x1685EA) };
        return new CGGradient(CGColorSpace.CreateSrgb() ?? CGColorSpace.CreateDeviceRGB(), colors.Select(color => color.CGColor).ToArray(), Locations);
    }

    /// <summary>Fills the current clip/mask with the brand gradient from the top-left to the bottom-right of <paramref name="rect"/> (UIKit coordinates).</summary>
    public static void FillGradient(CGContext context, CGRect rect, bool dark)
    {
        using var gradient = Gradient(dark);
        context.DrawLinearGradient(gradient, new CGPoint(rect.Left, rect.Top), new CGPoint(rect.Right, rect.Bottom),
            CGGradientDrawingOptions.DrawsBeforeStartLocation | CGGradientDrawingOptions.DrawsAfterEndLocation);
    }

    /// <summary>The promotion pill used in the not-subscribed card: white capsule, 12pt brand glyph, caption.</summary>
    public static UIView Chip(WinoIconGlyph glyph, string text)
    {
        var surface = new WinoSurfaceView
        {
            Fill = WinoStyle.Dynamic(UIColor.White, WinoStyle.Hex(0xFFFFFF, 0.08)),
            Stroke = WinoSettingsStyle.CardStroke,
            CornerRadius = 13,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var icon = new BrandIconView(glyph, 12);
        var label = WinoStyle.Label(text, WinoStyle.Caption, WinoStyle.PrimaryText);
        var row = WinoLayout.HStack(6, icon, label);
        WinoLayout.Fill(row, surface, 4, 10, 4, 10);
        surface.HeightAnchor.ConstraintGreaterThanOrEqualTo(26).Active = true;
        surface.IsAccessibilityElement = true;
        surface.AccessibilityLabel = text;
        surface.AccessibilityTraits = UIAccessibilityTrait.StaticText;
        return surface;
    }
}

/// <summary>A Wino glyph filled with the brand gradient instead of a flat tint.</summary>
public sealed class BrandIconView : UIView
{
    private string _glyph;
    private readonly double _size;

    public BrandIconView(WinoIconGlyph glyph, double size)
    {
        _glyph = WinoIcons.Glyph(glyph);
        _size = size;
        TranslatesAutoresizingMaskIntoConstraints = false;
        BackgroundColor = UIColor.Clear;
        Opaque = false;
        ContentMode = UIViewContentMode.Redraw;
        IsAccessibilityElement = false;
        WinoLayout.Size(this, size, size);
        SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
        SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Horizontal);
        WinoTraits.OnAppearanceChanged(this, static view => view.SetNeedsDisplay());
        WinoIcons.StyleChanged += StyleChanged;
    }

    public WinoIconGlyph Icon { set { _glyph = WinoIcons.Glyph(value); SetNeedsDisplay(); } }

    public override CGSize IntrinsicContentSize => new(_size, _size);

    private void StyleChanged(object? sender, EventArgs e) => SetNeedsDisplay();

    public override void Draw(CGRect rect)
    {
        if (UIGraphics.GetCurrentContext() is not { } context) return;
        var size = Math.Min(_size, Math.Min(Bounds.Width, Bounds.Height));
        var box = new CGRect(Bounds.GetMidX() - size / 2, Bounds.GetMidY() - size / 2, size, size);
        context.BeginTransparencyLayer();
        WinoIcons.Draw(_glyph, box, UIColor.Black, TraitCollection, false);
        context.SetBlendMode(CGBlendMode.SourceIn);
        WinoIntelligenceBrand.FillGradient(context, box, WinoIcons.IsDark(TraitCollection));
        context.EndTransparencyLayer();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoIcons.StyleChanged -= StyleChanged;
        base.Dispose(disposing);
    }
}

/// <summary>
/// Port of the Windows WinoIntelligencePromotionIllustration ("long thread, short summary"):
/// the same 128x128 canvas shapes and theme colours, scaled uniformly to the view.
/// </summary>
public sealed class WinoIntelligencePromotionIllustrationView : UIView
{
    public WinoIntelligencePromotionIllustrationView(double size = 96)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        BackgroundColor = UIColor.Clear;
        Opaque = false;
        ContentMode = UIViewContentMode.Redraw;
        WinoLayout.Size(this, size, size);
        IsAccessibilityElement = false;
        WinoTraits.OnAppearanceChanged(this, static view => view.SetNeedsDisplay());
    }

    public override void Draw(CGRect rect)
    {
        if (UIGraphics.GetCurrentContext() is not { } context) return;
        var dark = WinoIcons.IsDark(TraitCollection);
        var surface = dark ? WinoStyle.Hex(0x3C3C43) : UIColor.White;
        var line = dark ? WinoStyle.Hex(0x52525D) : WinoStyle.Hex(0xD6D6E0);
        var lineSoft = dark ? WinoStyle.Hex(0x46464F) : WinoStyle.Hex(0xE9E9F1);
        var warm = dark ? WinoStyle.Hex(0xF5C05C) : WinoStyle.Hex(0xF2B33D);
        var shadow = dark ? WinoStyle.Hex(0x000000, 0x52 / 255.0) : WinoStyle.Hex(0x141428, 0x12 / 255.0);
        var brand = dark ? WinoStyle.Hex(0x7FBAFF) : WinoStyle.Hex(0x3B84E8);

        var scale = (nfloat)(Math.Min(Bounds.Width, Bounds.Height) / 128.0);
        context.SaveState();
        context.TranslateCTM((Bounds.Width - 128 * scale) / 2, (Bounds.Height - 128 * scale) / 2);
        context.ScaleCTM(scale, scale);

        static void Fill(UIBezierPath path, UIColor color, double opacity = 1)
        {
            color.ColorWithAlpha((nfloat)(color.CGColor.Alpha * opacity)).SetFill();
            path.Fill();
        }
        static UIBezierPath Rect(double x, double y, double w, double h, double r) => UIBezierPath.FromRoundedRect(new CGRect(x, y, w, h), (nfloat)r);

        Fill(UIBezierPath.FromOval(new CGRect(28, 99, 68, 10)), shadow);
        Fill(Rect(28, 22, 78, 52, 10), brand, 0.2);
        var message = Rect(18, 38, 78, 60, 10);
        Fill(message, surface);
        line.SetStroke();
        message.LineWidth = 1.5f;
        message.Stroke();
        Fill(Rect(29, 50, 52, 4.5, 2.25), line);
        Fill(Rect(29, 59, 44, 4, 2), lineSoft);
        Fill(Rect(29, 68, 48, 4, 2), lineSoft);
        Fill(Rect(29, 80, 34, 5.5, 2.75), brand);
        Fill(Rect(29, 89, 22, 4.5, 2.25), brand, 0.45);
        Fill(UIBezierPath.FromOval(new CGRect(84, 12, 36, 36)), brand, 0.2);

        // The spark: the only shape that carries the brand gradient.
        var spark = Spark(102, 17, 115, 30, 102, 43, 89, 30, 2.08, 10.92);
        context.SaveState();
        spark.AddClip();
        WinoIntelligenceBrand.FillGradient(context, new CGRect(89, 17, 26, 26), dark);
        context.RestoreState();
        Fill(Spark(114, 45, 119, 50, 114, 55, 109, 50, 0.8, 4.2), warm);
        context.RestoreState();
    }

    /// <summary>The four-point star path from the XAML (quadratic curves through the inner control points).</summary>
    private static UIBezierPath Spark(double tx, double ty, double rx, double ry, double bx, double by, double lx, double ly, double near, double far)
    {
        var path = new UIBezierPath();
        path.MoveTo(new CGPoint(tx, ty));
        path.AddQuadCurveToPoint(new CGPoint(rx, ry), new CGPoint(tx + near, ty + far));
        path.AddQuadCurveToPoint(new CGPoint(bx, by), new CGPoint(tx + near, ry + near));
        path.AddQuadCurveToPoint(new CGPoint(lx, ly), new CGPoint(tx - near, ry + near));
        path.AddQuadCurveToPoint(new CGPoint(tx, ty), new CGPoint(tx - near, ty + far));
        path.ClosePath();
        return path;
    }
}
