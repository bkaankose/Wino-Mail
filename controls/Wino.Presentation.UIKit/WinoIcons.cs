using CoreGraphics;
using CoreText;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Presentation.UIKit;

/// <summary>
/// The WinoIcons fonts on iOS (UIKit twin of the AppKit <c>WinoIcons</c>). The app bundles the same three fonts as
/// Windows and the Mac (mono, colorful light, colorful dark) under <c>Fonts/</c>, and every Wino glyph is drawn from
/// them through the shared <see cref="WinoIconGlyph"/> identifiers. <see cref="Style"/> follows the "icon style"
/// preference.
/// </summary>
public static class WinoIcons
{
    private static string? _monoName, _lightName, _darkName;
    private static bool _registered;
    private static WinoIconStyle _style = WinoIconStyle.Monochrome;

    /// <summary>Raised after <see cref="Style"/> changes so long-lived views can redraw.</summary>
    public static event EventHandler? StyleChanged;

    /// <summary>Weak counterpart of <see cref="StyleChanged"/> for views that are created many times.</summary>
    public static WinoWeakEvent StyleWatchers { get; } = new();

    public static WinoIconStyle Style
    {
        get => _style;
        set
        {
            if (_style == value) return;
            _style = value;
            StyleChanged?.Invoke(null, EventArgs.Empty);
            StyleWatchers.Raise();
        }
    }

    public static bool IsColorful => _style == WinoIconStyle.Colorful;

    /// <summary>True once the mono font was found and registered.</summary>
    public static bool IsAvailable => _monoName is not null;

    /// <summary>Registers the fonts found in the app bundle's Fonts folder. Safe to call twice.</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        var folder = Path.Combine(NSBundle.MainBundle.ResourcePath ?? NSBundle.MainBundle.BundlePath, "Fonts");
        _monoName = RegisterFont(Path.Combine(folder, "WinoIcons.ttf"));
        _lightName = RegisterFont(Path.Combine(folder, "WinoIconsColor-Light.ttf")) ?? _monoName;
        _darkName = RegisterFont(Path.Combine(folder, "WinoIconsColor-Dark.ttf")) ?? _lightName;
    }

    private static string? RegisterFont(string path)
    {
        if (!File.Exists(path)) return null;
        using var url = NSUrl.FromFilename(path);
        CTFontManager.RegisterFontsForUrl(url, CTFontManagerScope.Process);
        var descriptors = CTFontManager.GetFonts(url);
        return descriptors is { Length: > 0 } ? descriptors[0].GetAttributes()?.Name : null;
    }

    /// <summary>The font that draws glyphs for the given traits and the current style.</summary>
    public static UIFont? Font(double size, UITraitCollection? traits = null, bool? colorful = null)
    {
        Register();
        string? name = _monoName;
        if (colorful ?? IsColorful) name = IsDark(traits) ? _darkName : _lightName;
        return name is null ? null : UIFont.FromName(name, (nfloat)size);
    }

    public static string Glyph(WinoIconGlyph icon) => WinoIconGlyphs.GetGlyph(icon);

    public static bool IsDark(UITraitCollection? traits) => WinoTraits.IsDark(traits);

    /// <summary>
    /// Draws one glyph so its em box fills <paramref name="rect"/> in the current UIKit graphics context (the fonts put
    /// the whole em between ascent and descent, so this centres the ink the same way Windows and the Mac do).
    /// </summary>
    public static void Draw(string glyph, CGRect rect, UIColor color, UITraitCollection? traits = null, bool? colorful = null)
    {
        double size = Math.Min(rect.Width, rect.Height);
        if (size <= 0 || string.IsNullOrEmpty(glyph) || Font(size, traits, colorful) is not { } font) return;
        var paragraph = new NSMutableParagraphStyle { Alignment = UITextAlignment.Center };
        var attributes = new UIStringAttributes { Font = font, ForegroundColor = color, ParagraphStyle = paragraph };
        double lineHeight = font.Ascender - font.Descender;
        var target = new CGRect(rect.X, rect.Y + (rect.Height - lineHeight) / 2, rect.Width, lineHeight);
        new NSAttributedString(glyph, attributes).DrawString(target);
    }

    /// <summary>
    /// A glyph as an image for buttons, bar items, menus and segments. Without a tint (mono style) the image is a
    /// template, so UIKit tints it like a symbol. With a tint, or in the colorful style, the image keeps its colours and
    /// carries a light and a dark variant, so it follows the appearance by itself.
    /// </summary>
    public static UIImage Image(WinoIconGlyph icon, double size = 16, UIColor? tint = null, string? accessibilityDescription = null)
        => Image(Glyph(icon), size, tint, accessibilityDescription);

    public static UIImage Image(string glyph, double size = 16, UIColor? tint = null, string? accessibilityDescription = null)
        => Image(glyph, size, tint, accessibilityDescription, null);

    /// <summary>A brand glyph in its palette colours whatever the icon style (the Google mark on its sign-in button).</summary>
    public static UIImage ColorfulImage(WinoIconGlyph icon, double size, string? accessibilityDescription = null)
        => Image(Glyph(icon), size, null, accessibilityDescription, true);

    /// <summary>
    /// A state glyph (star on and off, round check) drawn from the mono font in one colour whatever the icon style, as
    /// Windows draws these with an explicit Foreground.
    /// </summary>
    public static UIImage MonoImage(WinoIconGlyph icon, double size, UIColor tint, string? accessibilityDescription = null)
        => Image(Glyph(icon), size, tint, accessibilityDescription, false);

    /// <summary>
    /// The mono glyph as a template image whatever the icon style: bar buttons, tab items, toolbar rows and menu items,
    /// which UIKit tints like a symbol.
    /// </summary>
    public static UIImage TemplateImage(WinoIconGlyph icon, double size, string? accessibilityDescription = null)
    {
        var image = Render(Glyph(icon), size, UIColor.Black, UITraitCollection.FromUserInterfaceStyle(UIUserInterfaceStyle.Light), false)
            .ImageWithRenderingMode(UIImageRenderingMode.AlwaysTemplate);
        image.AccessibilityLabel = accessibilityDescription;
        return image;
    }

    private static UIImage Image(string glyph, double size, UIColor? tint, string? accessibilityDescription, bool? colorful)
    {
        bool template = tint is null && !(colorful ?? IsColorful);
        UIImage image;
        if (template)
        {
            image = Render(glyph, size, UIColor.Black, UITraitCollection.FromUserInterfaceStyle(UIUserInterfaceStyle.Light), false)
                .ImageWithRenderingMode(UIImageRenderingMode.AlwaysTemplate);
        }
        else
        {
            // The light and dark variants are registered and resolved with the display scale they were rendered at. An
            // asset variant registered without a display scale comes back at scale 1 once UIKit copies it (a template or
            // tinted copy in a bar or button), which showed the glyph three times too large on a 3x screen.
            var scale = Format.Scale;
            var light = WithScale(UITraitCollection.FromUserInterfaceStyle(UIUserInterfaceStyle.Light), scale);
            var dark = WithScale(UITraitCollection.FromUserInterfaceStyle(UIUserInterfaceStyle.Dark), scale);
            var color = tint ?? UIColor.Label;
            var asset = new UIImageAsset();
            asset.RegisterImage(Render(glyph, size, color.GetResolvedColor(light), light, colorful).ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal), light);
            asset.RegisterImage(Render(glyph, size, color.GetResolvedColor(dark), dark, colorful).ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal), dark);
            image = asset.FromTraitCollection(WithScale(UITraitCollection.CurrentTraitCollection, scale));
        }
        image.AccessibilityLabel = accessibilityDescription;
        return image;
    }

    private static UITraitCollection WithScale(UITraitCollection traits, nfloat scale)
        => traits.GetTraitCollectionByModifyingTraits(mutable => mutable.DisplayScale = scale);

    /// <summary>
    /// The renderer format: the device's display scale and a transparent background. Its scale is also the display scale
    /// the light and dark asset variants are registered with.
    /// </summary>
    private static UIGraphicsImageRendererFormat Format
    {
        get
        {
            var format = UIGraphicsImageRendererFormat.DefaultFormat;
            format.Opaque = false;
            return format;
        }
    }

    private static UIImage Render(string glyph, double size, UIColor color, UITraitCollection traits, bool? colorful)
    {
        var renderer = new UIGraphicsImageRenderer(new CGSize(size, size), Format);
        return renderer.CreateImage(_ => Draw(glyph, new CGRect(0, 0, size, size), color, traits, colorful));
    }
}

/// <summary>
/// A view that draws one Wino glyph (UIKit twin of the AppKit <c>WinoIconView</c>). It redraws after light, dark and
/// contrast changes and after icon style changes, so a colorful glyph picks the light or dark palette by itself.
/// A null tint uses the label colour.
/// </summary>
public class WinoIconView : UIView
{
    private string _glyph = string.Empty;
    private UIColor? _tint;
    private double _pointSize;
    private bool? _colorful;

    public WinoIconView(WinoIconGlyph icon = WinoIconGlyph.None, double pointSize = 16, UIColor? tint = null)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Opaque = false;
        BackgroundColor = UIColor.Clear;
        ContentMode = UIViewContentMode.Redraw;
        UserInteractionEnabled = false;
        _pointSize = pointSize;
        _tint = tint;
        if (icon != WinoIconGlyph.None) _glyph = WinoIcons.Glyph(icon);
        SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
        SetContentHuggingPriority(750, UILayoutConstraintAxis.Vertical);
        SetContentCompressionResistancePriority(750, UILayoutConstraintAxis.Horizontal);
        WinoTraits.OnAppearanceChanged(this, static view => view.SetNeedsDisplay());
        WinoIcons.StyleWatchers.Add(this, static view => view.SetNeedsDisplay());
    }

    public WinoIconGlyph Icon { set => Glyph = value == WinoIconGlyph.None ? string.Empty : WinoIcons.Glyph(value); }

    public string Glyph
    {
        get => _glyph;
        set { var glyph = value ?? string.Empty; if (_glyph == glyph) return; _glyph = glyph; SetNeedsDisplay(); }
    }

    public UIColor? Tint
    {
        get => _tint;
        set { _tint = value; SetNeedsDisplay(); }
    }

    public double PointSize
    {
        get => _pointSize;
        set { _pointSize = value; InvalidateIntrinsicContentSize(); SetNeedsDisplay(); }
    }

    /// <summary>Forces mono (false) or colorful (true); null follows <see cref="WinoIcons.Style"/>.</summary>
    public bool? Colorful
    {
        get => _colorful;
        set { _colorful = value; SetNeedsDisplay(); }
    }

    public override CGSize IntrinsicContentSize => new(_pointSize, _pointSize);

    public override void Draw(CGRect rect)
    {
        var size = Math.Min(_pointSize, Math.Min(Bounds.Width, Bounds.Height));
        var target = new CGRect(Bounds.GetMidX() - size / 2, Bounds.GetMidY() - size / 2, size, size);
        WinoIcons.Draw(_glyph, target, _tint ?? UIColor.Label, TraitCollection, _colorful);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoIcons.StyleWatchers.Remove(this);
        base.Dispose(disposing);
    }
}
