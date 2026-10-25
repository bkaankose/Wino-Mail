using AppKit;

namespace Wino.Presentation.AppKit;

/// <summary>
/// Wino's macOS design tokens. Colours are AppKit semantic colours so they follow light, dark,
/// increased contrast and the system accent without extra work; Wino adds spacing, shape and
/// type decisions on top (docs/macos-design-decisions.md).
/// </summary>
public static class WinoStyle
{
    // Platform-neutral values live in Wino.Presentation.WinoTokens; these names forward to them.
    public const double Space1 = WinoTokens.Space1;
    public const double Space2 = WinoTokens.Space2;
    public const double Space3 = WinoTokens.Space3;
    public const double Space4 = WinoTokens.Space4;
    public const double Space6 = WinoTokens.Space6;
    public const double Space8 = WinoTokens.Space8;

    public const double ControlRadius = WinoTokens.ControlRadius;
    public const double GroupRadius = WinoTokens.GroupRadius;
    public const double PopoverRadius = WinoTokens.PopoverRadius;

    public const double SettingsRowMinHeight = WinoTokens.SettingsRowMinHeight;
    public const double SidebarRowHeight = WinoTokens.SidebarRowHeight;

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
        set { _accentOverride = value; AccentChanged?.Invoke(null, EventArgs.Empty); AccentWatchers.Raise(); }
    }

    /// <summary>Weak counterpart of <see cref="AccentChanged"/> for views that are created many times.</summary>
    public static WinoWeakEvent AccentWatchers { get; } = new();

    /// <summary>Weak counterpart of <see cref="BackdropChanged"/> for views that are created many times.</summary>
    public static WinoWeakEvent BackdropWatchers { get; } = new();

    private static NSColor? _accentOverride;

    /// <summary>Raised on the UI thread after the accent changes, so long-lived views can refresh.</summary>
    public static event EventHandler? AccentChanged;

    /// <summary>
    /// Draws a select-one segmented control's selected segment in the theme accent rather than the
    /// system accent, so it matches the accent-tinted glyphs and pills around it, and follows accent
    /// changes for as long as the control lives.
    /// </summary>
    public static void UseAccentSelection(NSSegmentedControl control)
    {
        control.SelectedSegmentBezelColor = Accent;
        var target = new WeakReference<NSSegmentedControl>(control);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (target.TryGetTarget(out var live) && live.Handle != IntPtr.Zero) live.SelectedSegmentBezelColor = Accent;
            else AccentChanged -= handler;
        };
        AccentChanged += handler;
    }

    /// <summary>
    /// True while a Wino theme paints the window (wallpaper or gradient). Panes then float as
    /// Wino zones (<see cref="WinoZoneView"/>) and lists draw on a clear background.
    /// </summary>
    public static bool HasBackdrop
    {
        get => _hasBackdrop;
        set { _hasBackdrop = value; BackdropChanged?.Invoke(null, EventArgs.Empty); BackdropWatchers.Raise(); }
    }

    private static bool _hasBackdrop;

    /// <summary>
    /// True when the backdrop is only the translucent window material (no theme wallpaper or gradient).
    /// Set before <see cref="HasBackdrop"/> so <see cref="BackdropChanged"/> handlers read both.
    /// </summary>
    public static bool IsWindowMaterialOnly { get; set; }

    /// <summary>Raised on the UI thread after the theme backdrop changes.</summary>
    public static event EventHandler? BackdropChanged;

    /// <summary>
    /// True while a wallpaper or gradient theme paints the window: panes then float as rounded zones
    /// with gutters. Otherwise panes run edge to edge with hairline dividers, like a native split view.
    /// </summary>
    public static bool FloatingZones => HasBackdrop && !IsWindowMaterialOnly;

    /// <summary>Gap between the window edge, the sidebar and the zones (Windows uses 6).</summary>
    public const double ZoneGutter = WinoTokens.ZoneGutter;
    public const double ZoneRadius = WinoTokens.ZoneRadius;

    /// <summary>
    /// The Windows WinoContentZoneBackgroud: opaque white in light, a translucent layer in dark
    /// so the wallpaper glows through the reader and list.
    /// </summary>
    public static NSColor ZoneFill { get; } = WinoThemeSurfaces.Dynamic(WinoThemeSurface.Workspace, Dynamic(NSColor.White, Hex(0x2B2B2F, 0.82)));
    public static NSColor ZoneStroke { get; } = Dynamic(Hex(0x000000, 0.08), Hex(0xFFFFFF, 0.08));

    /// <summary>A colour that resolves per appearance.</summary>
    public static NSColor Dynamic(NSColor light, NSColor dark)
        => NSColor.GetColor(string.Empty, appearance =>
            appearance.FindBestMatch([NSAppearance.NameAqua, NSAppearance.NameDarkAqua]) == NSAppearance.NameDarkAqua ? dark : light);

    /// <summary>
    /// Black or white text at <paramref name="opacity"/>, resolved per appearance. <c>NSColor.Label.ColorWithAlphaComponent</c>
    /// resolves against the app appearance when it is created, so a light-themed window over a dark system
    /// drew it white.
    /// </summary>
    public static NSColor LabelWithOpacity(double opacity)
        => Dynamic(NSColor.Black.ColorWithAlphaComponent((nfloat)opacity), NSColor.White.ColorWithAlphaComponent((nfloat)opacity));

    /// <summary>The row hover fill shared by the pane, list and settings rows (cached: dynamic colours resolve at draw time).</summary>
    public static NSColor RowHoverFill { get; } = Dynamic(Hex(0x000000, 0.035), Hex(0xFFFFFF, 0.06));

    /// <summary>The row selection fill of the pane and settings rows.</summary>
    public static NSColor RowSelectionFill { get; } = Dynamic(Hex(0x000000, 0.07), Hex(0xFFFFFF, 0.11));

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

    /// <summary>The 16-step avatar palette from the Wino design system (<see cref="WinoTokens.AvatarPalette"/>).</summary>
    public static readonly NSColor[] AvatarPalette = WinoTokens.AvatarPalette.Select(value => ToColor(WinoRgba.Parse(value))).ToArray();

    public static NSColor Hex(uint rgb, double alpha = 1) => ToColor(WinoRgba.FromRgb(rgb, alpha));

    /// <summary>Parses #RRGGBB or #AARRGGBB; returns null for anything else.</summary>
    public static NSColor? FromHexString(string? value)
        => WinoRgba.FromHexString(value) is { } color ? ToColor(color) : null;

    public static NSColor ToColor(WinoRgba color)
        => NSColor.FromSrgb((nfloat)(color.R / 255.0), (nfloat)(color.G / 255.0), (nfloat)(color.B / 255.0), (nfloat)color.Alpha);

    public static NSColor AvatarColor(string? key) => AvatarPalette[WinoTokens.AvatarPaletteIndex(key)];

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
