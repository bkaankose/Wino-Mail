using AppKit;
using CoreGraphics;
using CoreText;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Presentation.AppKit;

/// <summary>
/// The WinoIcons fonts on macOS. The app bundles the same three fonts as Windows (mono, colorful
/// light, colorful dark) and every Wino glyph is drawn from them, so a folder, command or provider
/// looks the same on both platforms. <see cref="Style"/> follows the Windows "icon style" preference.
/// </summary>
public static class WinoIcons
{
    private static string? _monoName, _lightName, _darkName;
    private static WinoIconStyle _style = WinoIconStyle.Monochrome;

    /// <summary>Raised after <see cref="Style"/> changes so long-lived views can redraw.</summary>
    public static event EventHandler? StyleChanged;

    public static WinoIconStyle Style
    {
        get => _style;
        set
        {
            if (_style == value) return;
            _style = value;
            StyleChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static bool IsColorful => _style == WinoIconStyle.Colorful;

    /// <summary>Registers the fonts found in the app bundle's Resources/Fonts folder. Safe to call twice.</summary>
    public static void Register()
    {
        if (_monoName is not null) return;
        var folder = Path.Combine(NSBundle.MainBundle.ResourcePath ?? string.Empty, "Fonts");
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
        return descriptors is { Length: > 0 } ? descriptors[0].GetAttributes().Name : null;
    }

    /// <summary>The font that draws glyphs for the given appearance and the current style.</summary>
    public static NSFont? Font(double size, NSAppearance? appearance = null, bool? colorful = null)
    {
        Register();
        string? name = _monoName;
        if (colorful ?? IsColorful) name = IsDark(appearance) ? _darkName : _lightName;
        return name is null ? null : NSFont.FromFontName(name, (nfloat)size);
    }

    public static string Glyph(WinoIconGlyph icon) => WinoIconGlyphs.GetGlyph(icon);

    public static bool IsDark(NSAppearance? appearance)
    {
        appearance ??= NSAppearance.CurrentDrawingAppearance ?? NSApplication.SharedApplication.EffectiveAppearance;
        return appearance.FindBestMatch([NSAppearance.NameAqua, NSAppearance.NameDarkAqua]) == NSAppearance.NameDarkAqua;
    }

    /// <summary>
    /// Draws one glyph so its em box fills <paramref name="rect"/> (the fonts put the whole
    /// em between ascent and descent, so this centres the ink the same way Windows does).
    /// </summary>
    public static void Draw(string glyph, CGRect rect, NSColor color, NSAppearance? appearance = null, bool? colorful = null)
    {
        double size = Math.Min(rect.Width, rect.Height);
        if (size <= 0 || string.IsNullOrEmpty(glyph) || Font(size, appearance, colorful) is not { } font) return;
        var paragraph = new NSMutableParagraphStyle { Alignment = NSTextAlignment.Center };
        var attributes = new NSStringAttributes { Font = font, ForegroundColor = color, ParagraphStyle = paragraph };
        double lineHeight = font.Ascender - font.Descender;
        var target = new CGRect(rect.X, rect.Y + (rect.Height - lineHeight) / 2, rect.Width, lineHeight);
        new NSAttributedString(glyph, attributes).DrawInRect(target);
    }

    /// <summary>
    /// A glyph as an image for buttons, menus and toolbar items. Without a tint the image is a
    /// template, so AppKit tints it like an SF Symbol; colorful layers keep their palette colours.
    /// </summary>
    public static NSImage Image(WinoIconGlyph icon, double size = 16, NSColor? tint = null, string? accessibilityDescription = null)
        => Image(Glyph(icon), size, tint, accessibilityDescription);

    public static NSImage Image(string glyph, double size = 16, NSColor? tint = null, string? accessibilityDescription = null)
    {
        bool template = tint is null && !IsColorful;
        var image = NSImage.ImageWithSize(new CGSize(size, size), false, rect =>
        {
            var color = tint ?? (template ? NSColor.Black : NSColor.Label);
            Draw(glyph, rect, color);
            return true;
        });
        image.Template = template;
        image.AccessibilityDescription = accessibilityDescription;
        return image;
    }
}

/// <summary>
/// A view that draws one Wino glyph. It redraws on appearance and icon style changes, so a
/// colorful glyph picks the light or dark palette by itself. A null tint uses the label colour.
/// </summary>
public class WinoIconView : NSView
{
    private string _glyph = string.Empty;
    private NSColor? _tint;
    private double _pointSize;
    private bool? _colorful;

    public WinoIconView(WinoIconGlyph icon = WinoIconGlyph.None, double pointSize = 16, NSColor? tint = null)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _pointSize = pointSize;
        _tint = tint;
        if (icon != WinoIconGlyph.None) _glyph = WinoIcons.Glyph(icon);
        SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Vertical);
        SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        WinoIcons.StyleChanged += IconStyleChanged;
    }

    public WinoIconGlyph Icon { set => Glyph = value == WinoIconGlyph.None ? string.Empty : WinoIcons.Glyph(value); }

    public string Glyph
    {
        get => _glyph;
        set { if (_glyph == value) return; _glyph = value ?? string.Empty; NeedsDisplay = true; }
    }

    public NSColor? Tint
    {
        get => _tint;
        set { _tint = value; NeedsDisplay = true; }
    }

    public double PointSize
    {
        get => _pointSize;
        set { _pointSize = value; InvalidateIntrinsicContentSize(); NeedsDisplay = true; }
    }

    /// <summary>Forces mono (false) or colorful (true); null follows <see cref="WinoIcons.Style"/>.</summary>
    public bool? Colorful
    {
        get => _colorful;
        set { _colorful = value; NeedsDisplay = true; }
    }

    public override CGSize IntrinsicContentSize => new(_pointSize, _pointSize);

    public override bool IsFlipped => false;

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        NeedsDisplay = true;
    }

    private void IconStyleChanged(object? sender, EventArgs args) => NeedsDisplay = true;

    public override void DrawRect(CGRect dirtyRect)
    {
        var size = Math.Min(_pointSize, Math.Min(Bounds.Width, Bounds.Height));
        var rect = new CGRect(Bounds.GetMidX() - size / 2, Bounds.GetMidY() - size / 2, size, size);
        WinoIcons.Draw(_glyph, rect, _tint ?? NSColor.Label, EffectiveAppearance, _colorful);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoIcons.StyleChanged -= IconStyleChanged;
        base.Dispose(disposing);
    }
}
