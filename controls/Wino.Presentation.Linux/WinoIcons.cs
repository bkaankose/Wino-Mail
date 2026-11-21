using System.Text;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux.Native;
using Wino.Presentation.Linux.Style;

namespace Wino.Presentation.Linux;

public enum WinoIconMode
{
    /// <summary>Always the monochrome font: commands, toolbar and menu glyphs.</summary>
    Mono,

    /// <summary>The colour font when the icon style is colourful: places, things and state.</summary>
    FollowStyle,

    /// <summary>Always the colour font: brand marks.</summary>
    Colorful,
}

public readonly record struct WinoIconFontCheck(bool Ok, string Detail);

/// <summary>
/// The WinoIcons fonts: registration for this process only, the stylesheet that selects the monochrome or the Light or
/// Dark colour font, and labels and buttons that show a glyph. Light, dark, high contrast and icon-style changes are
/// one stylesheet reload; no icon holds a handler.
/// </summary>
public static class WinoIcons
{
    public const string SetFamily = "WinoIconsMaterialRounded";
    public const string MonoClass = "wino-icon";
    public const string FollowClass = "wino-icon-follow";
    public const string ColorClass = "wino-icon-color";

    private static readonly string[] FontFiles = ["WinoIcons.ttf", "WinoIconsColor-Light.ttf", "WinoIconsColor-Dark.ttf"];
    private static readonly int[] Sizes = [.. Enumerable.Range(8, 57), 72, 80, 96, 128];

    private static WinoIconStyle _style = WinoIconStyle.Monochrome;
    private static bool _registered;
    private static string _registrationDetail = "the fonts were not registered";
    private static string? _parsingError;
    private static Gtk.CssProvider? _provider;
    private static readonly List<IDisposable> StyleWatches = [];

    public static WinoIconStyle Style
    {
        get => _style;
        set
        {
            if (_style == value) return;
            _style = value;
            Refresh();
        }
    }

    public static bool IsColorful => _style == WinoIconStyle.Colorful && !HighContrast;

    /// <summary>Raised after the style, the dark flag or high contrast changed, for widgets that draw glyphs themselves.</summary>
    public static WinoWeakEvent Changed { get; } = new();

    /// <summary>
    /// Registers the three fonts in <paramref name="fontsDirectory"/> for this process. Call it on the main thread
    /// before the application object exists; false when a file is missing or was rejected.
    /// </summary>
    public static bool Register(string fontsDirectory)
    {
        if (_registered) return true;
        try
        {
            PangoCairo.Module.Initialize();
            var useFontMap = Pango.Functions.VersionCheck(1, 56, 0) is null;
            var map = useFontMap ? PangoCairo.Functions.FontMapGetDefault() : null;
            foreach (var file in FontFiles)
            {
                var path = Path.Combine(fontsDirectory, file);
                if (!File.Exists(path))
                {
                    _registrationDetail = $"{path} is missing";
                    return false;
                }
                var added = map is not null ? map.AddFontFile(path) : FontRegistration.AddApplicationFont(path);
                if (!added)
                {
                    _registrationDetail = $"{(map is not null ? "Pango" : "fontconfig")} rejected {path}";
                    return false;
                }
            }
            _registered = true;
            _registrationDetail = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or GLib.GException)
        {
            _registrationDetail = exception.Message;
            return false;
        }
    }

    /// <summary>Adds the icon stylesheet to the default display. Harmless before a display exists and when called again.</summary>
    public static void Install()
    {
        if (_provider is not null || Gdk.Display.GetDefault() is not { } display) return;
        var provider = Gtk.CssProvider.New();
        provider.OnParsingError += static (_, args) => _parsingError = args.Error.Message;
        provider.LoadFromString(BuildCss());
        Gtk.StyleContext.AddProviderForDisplay(display, provider, WinoStyle.ApplicationPriority);
        _provider = provider;

        // The style manager is a process singleton, so these two subscriptions live as long as the process.
        var manager = Adw.StyleManager.GetDefault();
        StyleWatches.Add(GtkSignals.OnNotify(manager, "dark", Refresh));
        StyleWatches.Add(GtkSignals.OnNotify(manager, "high-contrast", Refresh));
    }

    /// <summary>Resolves the three families through Pango; the start log reports the result.</summary>
    public static WinoIconFontCheck Verify()
    {
        if (!_registered) return new WinoIconFontCheck(false, _registrationDetail);
        var context = PangoCairo.Functions.FontMapGetDefault().CreateContext();
        var detail = new StringBuilder("MaterialRounded:");
        var ok = true;
        foreach (var (name, family) in ((string Name, string Family)[])[("mono", SetFamily), ("light", SetFamily + "ColorLight"), ("dark", SetFamily + "ColorDark")])
        {
            var resolved = context.LoadFont(Pango.FontDescription.FromString(family))?.Describe().GetFamily();
            ok &= string.Equals(resolved, family, StringComparison.Ordinal);
            detail.Append(' ').Append(name).Append('=').Append(resolved ?? "none");
        }
        if (_parsingError is not null) detail.Append("; CSS error: ").Append(_parsingError);
        return new WinoIconFontCheck(ok && _parsingError is null, detail.ToString());
    }

    public static string Glyph(WinoIconGlyph icon) => icon == WinoIconGlyph.None ? string.Empty : WinoIconGlyphs.GetGlyph(icon);

    public static string SizeClass(int size)
    {
        var supported = Sizes[0];
        foreach (var candidate in Sizes)
        {
            if (candidate > size) break;
            supported = candidate;
        }
        return "wino-icon-s" + supported.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A monochrome glyph label, hidden from assistive technology.</summary>
    public static Gtk.Label Label(WinoIconGlyph icon, int size = 16) => Label(Glyph(icon), size);

    public static Gtk.Label Label(string glyph, int size = 16) => GlyphLabel(glyph, size, MonoClass);

    /// <summary>
    /// Sets the glyph, tinted with <paramref name="tintHex"/> (<c>#RRGGBB</c>) when given. Only glyph strings may pass
    /// through here: a tinted glyph is set as markup.
    /// </summary>
    public static void SetGlyph(Gtk.Label label, string glyph, string? tintHex = null)
    {
        if (IsHexColor(tintHex)) label.SetMarkup($"<span foreground=\"{tintHex}\">{glyph}</span>");
        else label.SetText(glyph);
    }

    /// <summary>An icon-only button; the accessible label is also the tooltip.</summary>
    public static Gtk.Button Button(WinoIconGlyph icon, string accessibleLabel, int size = 16, bool flat = true)
        => Decorate(Gtk.Button.New(), icon, accessibleLabel, size, flat);

    public static Gtk.ToggleButton ToggleButton(WinoIconGlyph icon, string accessibleLabel, int size = 16, bool flat = true)
        => Decorate(Gtk.ToggleButton.New(), icon, accessibleLabel, size, flat);

    /// <summary>A glyph and a text side by side, for the caller's own button.</summary>
    public static Gtk.Box ButtonContent(WinoIconGlyph icon, string text, int size = 16)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        box.Append(Label(icon, size));
        box.Append(Gtk.Label.New(text));
        return box;
    }

    internal static Gtk.Label GlyphLabel(string glyph, int size, string fontClass)
    {
        var label = Gtk.Label.New(glyph);
        // A private-use character must never be read out; the role can only be set before the widget is realized.
        label.AccessibleRole = Gtk.AccessibleRole.Presentation;
        label.AddCssClass(fontClass);
        label.AddCssClass(SizeClass(size));
        label.SetHalign(Gtk.Align.Center);
        label.SetValign(Gtk.Align.Center);
        return label;
    }

    private static T Decorate<T>(T button, WinoIconGlyph icon, string accessibleLabel, int size, bool flat) where T : Gtk.Button
    {
        if (string.IsNullOrWhiteSpace(accessibleLabel)) throw new ArgumentException("An icon-only button needs an accessible label.", nameof(accessibleLabel));
        button.SetChild(Label(icon, size));
        if (flat) button.AddCssClass("flat");
        button.SetValign(Gtk.Align.Center);
        return WinoA11y.LabelWithTooltip(button, accessibleLabel);
    }

    private static bool HighContrast => _provider is not null && Adw.StyleManager.GetDefault().GetHighContrast();

    private static bool IsHexColor(string? value)
        => value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    private static void Refresh()
    {
        _provider?.LoadFromString(BuildCss());
        Changed.Raise();
    }

    private static string BuildCss()
    {
        var colorFamily = SetFamily + (Adw.StyleManager.GetDefault().GetDark() ? "ColorDark" : "ColorLight");
        var highContrast = HighContrast;
        var css = new StringBuilder()
            .Append(".wino-icon, .wino-icon-follow, .wino-icon-color { font-weight: normal; font-style: normal; font-feature-settings: normal; letter-spacing: 0; }\n")
            .Append(".wino-icon { font-family: \"").Append(SetFamily).Append("\"; }\n")
            .Append(".wino-icon-color { font-family: \"").Append(highContrast ? SetFamily : colorFamily).Append("\"; }\n")
            .Append(".wino-icon-follow { font-family: \"").Append(colorFamily).Append("\"; opacity: ").Append(IsColorful ? '1' : '0').Append("; }\n");
        foreach (var size in Sizes)
        {
            var value = size.ToString(System.Globalization.CultureInfo.InvariantCulture);
            css.Append(".wino-icon-s").Append(value).Append(" { font-size: ").Append(value).Append("px; }\n");
        }
        return css.ToString();
    }
}
