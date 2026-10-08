using AppKit;

namespace Wino.Presentation.AppKit;

/// <summary>
/// Wino's macOS design tokens. Colours are AppKit semantic colours so they follow light, dark,
/// increased contrast and the system accent without extra work; Wino adds spacing, shape and
/// type decisions on top (docs/macos-design-decisions.md).
/// </summary>
public static class WinoStyle
{
    public const double Space1 = 4;
    public const double Space2 = 8;
    public const double Space3 = 12;
    public const double Space4 = 16;
    public const double Space6 = 24;
    public const double Space8 = 32;

    public const double ControlRadius = 6;
    public const double GroupRadius = 8;
    public const double PopoverRadius = 10;

    public const double SettingsRowMinHeight = 44;
    public const double SidebarRowHeight = 28;

    public static NSFont Body => NSFont.SystemFontOfSize(13);
    public static NSFont BodyMedium => NSFont.SystemFontOfSize(13, NSFontWeight.Medium);
    public static NSFont BodyStrong => NSFont.SystemFontOfSize(13, NSFontWeight.Semibold);
    public static NSFont Caption => NSFont.SystemFontOfSize(11);
    public static NSFont CaptionStrong => NSFont.SystemFontOfSize(11, NSFontWeight.Semibold);
    public static NSFont Description => NSFont.SystemFontOfSize((nfloat)11.5);
    public static NSFont Heading => NSFont.SystemFontOfSize(15, NSFontWeight.Bold);
    public static NSFont PageTitle => NSFont.SystemFontOfSize(20, NSFontWeight.Bold);
    public static NSFont ReaderSubject => NSFont.SystemFontOfSize(19, NSFontWeight.Bold);

    public static NSColor PrimaryText => NSColor.Label;
    public static NSColor SecondaryText => NSColor.SecondaryLabel;
    public static NSColor TertiaryText => NSColor.TertiaryLabel;
    public static NSColor Separator => NSColor.Separator;
    /// <summary>The Wino theme accent when a theme sets one, otherwise the system accent.</summary>
    public static NSColor Accent => AccentOverride ?? NSColor.ControlAccent;

    /// <summary>Set by the theme service. Views read <see cref="Accent"/> when they configure themselves.</summary>
    public static NSColor? AccentOverride
    {
        get => _accentOverride;
        set { _accentOverride = value; AccentChanged?.Invoke(null, EventArgs.Empty); }
    }

    private static NSColor? _accentOverride;

    /// <summary>Raised on the UI thread after the accent changes, so long-lived views can refresh.</summary>
    public static event EventHandler? AccentChanged;

    /// <summary>
    /// True while a Wino theme paints the window (wallpaper or gradient). Panes then float as
    /// Wino zones (<see cref="WinoZoneView"/>) and lists draw on a clear background.
    /// </summary>
    public static bool HasBackdrop
    {
        get => _hasBackdrop;
        set { _hasBackdrop = value; BackdropChanged?.Invoke(null, EventArgs.Empty); }
    }

    private static bool _hasBackdrop;

    /// <summary>Raised on the UI thread after the theme backdrop changes.</summary>
    public static event EventHandler? BackdropChanged;

    /// <summary>Gap between the window edge, the sidebar and the zones (Windows uses 6).</summary>
    public const double ZoneGutter = 6;
    public const double ZoneRadius = 8;

    /// <summary>
    /// The Windows WinoContentZoneBackgroud: opaque white in light, a translucent layer in dark
    /// so the wallpaper glows through the reader and list.
    /// </summary>
    public static NSColor ZoneFill => Dynamic(NSColor.White, Hex(0x2B2B2F, 0.82));
    public static NSColor ZoneStroke => Dynamic(Hex(0x000000, 0.08), Hex(0xFFFFFF, 0.08));

    /// <summary>A colour that resolves per appearance.</summary>
    public static NSColor Dynamic(NSColor light, NSColor dark)
        => NSColor.GetColor(string.Empty, appearance =>
            appearance.FindBestMatch([NSAppearance.NameAqua, NSAppearance.NameDarkAqua]) == NSAppearance.NameDarkAqua ? dark : light);

    /// <summary>Grouped settings rows and cards: the System Settings group surface.</summary>
    public static NSColor GroupFill => NSColor.ControlBackground;
    public static NSColor GroupStroke => NSColor.Separator;
    public static NSColor SubtleFill => NSColor.QuaternarySystemFill;

    /// <summary>Wino status hues used for glyphs and banner tints, never for body text.</summary>
    public static NSColor Flagged => NSColor.SystemRed;
    public static NSColor Caution => NSColor.SystemOrange;
    public static NSColor Success => NSColor.SystemGreen;
    public static NSColor Critical => NSColor.SystemRed;
    public static NSColor Informational => NSColor.SystemBlue;

    /// <summary>The 16-step avatar palette from the Wino design system.</summary>
    public static readonly NSColor[] AvatarPalette =
    [
        Hex(0xE74C3C), Hex(0xC0392B), Hex(0x9B59B6), Hex(0x8E44AD),
        Hex(0x3498DB), Hex(0x2980B9), Hex(0x16A085), Hex(0x27AE60),
        Hex(0x00796B), Hex(0xE67E22), Hex(0xD35400), Hex(0x663399),
        Hex(0x3F51B5), Hex(0xE91E63), Hex(0x7F8C8D), Hex(0x795548)
    ];

    public static NSColor Hex(uint rgb, double alpha = 1)
        => NSColor.FromSrgb((nfloat)(((rgb >> 16) & 0xFF) / 255.0), (nfloat)(((rgb >> 8) & 0xFF) / 255.0),
            (nfloat)((rgb & 0xFF) / 255.0), (nfloat)alpha);

    /// <summary>Parses #RRGGBB or #AARRGGBB; returns null for anything else.</summary>
    public static NSColor? FromHexString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim().TrimStart('#');
        if (!uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var parsed)) return null;
        return text.Length switch
        {
            6 => Hex(parsed),
            8 => Hex(parsed & 0xFFFFFF, ((parsed >> 24) & 0xFF) / 255.0),
            _ => null
        };
    }

    public static NSColor AvatarColor(string? key)
    {
        if (string.IsNullOrEmpty(key)) return AvatarPalette[14];
        int hash = 17;
        foreach (char character in key.ToLowerInvariant()) hash = unchecked(hash * 31 + character);
        return AvatarPalette[(int)((uint)hash % (uint)AvatarPalette.Length)];
    }

    public static NSTextField Label(string? text, NSFont? font = null, NSColor? color = null, int maximumLines = 1)
    {
        var label = NSTextField.CreateLabel(text ?? string.Empty);
        label.Font = font ?? Body;
        label.TextColor = color ?? PrimaryText;
        label.MaximumNumberOfLines = maximumLines;
        label.LineBreakMode = maximumLines == 1 ? NSLineBreakMode.TruncatingTail : NSLineBreakMode.ByWordWrapping;
        label.TranslatesAutoresizingMaskIntoConstraints = false;
        if (maximumLines != 1) label.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        return label;
    }

    /// <summary>An SF Symbol image, sized for its context. Returns null on an unknown name.</summary>
    public static NSImage? Symbol(string name, string? accessibilityDescription = null, double pointSize = 0, nfloat? weight = null)
    {
        var image = NSImage.GetSystemSymbol(name, accessibilityDescription);
        if (image is null || pointSize <= 0) return image;
        var configuration = NSImageSymbolConfiguration.Create((nfloat)pointSize, weight ?? NSFontWeight.Regular);
        return image.GetImage(configuration) ?? image;
    }

    public static NSImageView SymbolView(string name, string? accessibilityDescription = null, double pointSize = 14, NSColor? tint = null)
    {
        var view = new NSImageView
        {
            Image = Symbol(name, accessibilityDescription, pointSize),
            ContentTintColor = tint ?? SecondaryText,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        view.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        return view;
    }
}

/// <summary>
/// Accessibility setters. Several AppKit controls (NSButton, NSOutlineView, ...) redeclare the
/// accessibility properties read-only; setting through NSView reaches the same Objective-C setter.
/// </summary>
public static class WinoAccessibility
{
    public static T Label<T>(T view, string? label) where T : NSView
    {
        ((NSView)view).AccessibilityLabel = label;
        return view;
    }

    public static T Help<T>(T view, string? help) where T : NSView
    {
        ((NSView)view).AccessibilityHelp = help;
        return view;
    }
}
