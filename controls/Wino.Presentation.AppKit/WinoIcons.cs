using AppKit;
using CoreGraphics;
using CoreText;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;

namespace Wino.Presentation.AppKit;

/// <summary>
/// The WinoIcons fonts on macOS. The app bundles the same fonts as Windows: every icon set has a mono, a
/// colorful light and a colorful dark font with the same codepoints, and every Wino glyph is drawn from them.
/// <see cref="Set"/> and <see cref="Style"/> follow the "icon set" and "icon style" preferences.
/// </summary>
public static class WinoIcons
{
    // Font names (mono, light, dark) per set, registered the first time a set is drawn.
    private static readonly Dictionary<WinoIconSet, string?[]> s_names = [];
    private static WinoIconStyle _style = WinoIconStyle.Monochrome;
    private static WinoIconSet _set = WinoIconSet.Classic;

    /// <summary>Raised after <see cref="Style"/> or <see cref="Set"/> changes so long-lived views can redraw.</summary>
    public static event EventHandler? StyleChanged;

    public static WinoIconSet Set
    {
        get => _set;
        set
        {
            if (_set == value) return;
            _set = value;
            StyleChanged?.Invoke(null, EventArgs.Empty);
        }
    }

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

    /// <summary>Registers the current set's fonts from the app bundle's Resources/Fonts folder. Safe to call twice.</summary>
    public static void Register() => Names(_set);

    /// <summary>The registered font names of <paramref name="set"/>; a set whose fonts are missing draws with Classic.</summary>
    private static string?[] Names(WinoIconSet set)
    {
        if (s_names.TryGetValue(set, out var names)) return names;
        var folder = Path.Combine(NSBundle.MainBundle.ResourcePath ?? string.Empty, "Fonts");
        var files = WinoIconFonts.FileNames(set);
        var mono = RegisterFont(Path.Combine(folder, files[0]));
        var light = RegisterFont(Path.Combine(folder, files[1])) ?? mono;
        var dark = RegisterFont(Path.Combine(folder, files[2])) ?? light;
        names = mono is null && set != WinoIconSet.Classic ? Names(WinoIconSet.Classic) : [mono, light, dark];
        s_names[set] = names;
        return names;
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
        var names = Names(_set);
        string? name = names[0];
        if (colorful ?? IsColorful) name = IsDark(appearance) ? names[2] : names[1];
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
        => Image(glyph, size, tint, accessibilityDescription, null);

    /// <summary>A brand glyph in its palette colours whatever the icon style (the Google mark on its sign-in button).</summary>
    public static NSImage ColorfulImage(WinoIconGlyph icon, double size, string? accessibilityDescription = null)
        => Image(Glyph(icon), size, null, accessibilityDescription, true);

    private static NSImage Image(string glyph, double size, NSColor? tint, string? accessibilityDescription, bool? colorful)
    {
        bool template = tint is null && !(colorful ?? IsColorful);
        var image = NSImage.ImageWithSize(new CGSize(size, size), false, rect =>
        {
            var color = tint ?? (template ? NSColor.Black : NSColor.Label);
            Draw(glyph, rect, color, null, colorful);
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
