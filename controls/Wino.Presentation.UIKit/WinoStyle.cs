using UIKit;

namespace Wino.Presentation.UIKit;

/// <summary>
/// Wino's iOS design tokens, the UIKit twin of the AppKit <c>WinoStyle</c>. Colours are UIKit semantic colours or
/// dynamic providers so they follow light, dark and increased contrast; spacing and shape forward to
/// <see cref="WinoTokens"/>. Fonts use the iOS text sizes and scale with Dynamic Type.
/// </summary>
public static class WinoStyle
{
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

    /// <summary>Gap between the screen edge, the sidebar and the zones.</summary>
    public const double ZoneGutter = WinoTokens.ZoneGutter;

    /// <summary>The iOS zone radius (iOS design decisions: one rounded Wino zone, radius 10).</summary>
    public const double ZoneRadius = 10;

    /// <summary>Minimum touch target (Human Interface Guidelines).</summary>
    public const double MinimumHitTarget = 44;

    public static UIFont Body => Scaled(17);
    public static UIFont BodyMedium => Scaled(17, UIFontWeight.Medium);
    public static UIFont BodyStrong => Scaled(17, UIFontWeight.Semibold);
    public static UIFont Caption => Scaled(12);
    public static UIFont CaptionStrong => Scaled(12, UIFontWeight.Semibold);
    public static UIFont Description => Scaled(13);
    public static UIFont Subheadline => Scaled(15);
    public static UIFont Heading => Scaled(20, UIFontWeight.Bold);
    public static UIFont PageTitle => Scaled(28, UIFontWeight.Bold);
    public static UIFont ReaderSubject => Scaled(22, UIFontWeight.Bold);

    /// <summary>The system font at <paramref name="size"/> points, scaled for the user's text size.</summary>
    public static UIFont Scaled(double size, UIFontWeight weight = UIFontWeight.Regular)
        => UIFontMetrics.DefaultMetrics.GetScaledFont(UIFont.SystemFontOfSize((nfloat)size, weight));

    /// <summary>The system font at a fixed size (dense rows whose height is measured, glyph badges).</summary>
    public static UIFont Fixed(double size, UIFontWeight weight = UIFontWeight.Regular) => UIFont.SystemFontOfSize((nfloat)size, weight);

    public static UIColor PrimaryText => UIColor.Label;
    public static UIColor SecondaryText => UIColor.SecondaryLabel;
    public static UIColor TertiaryText => UIColor.TertiaryLabel;
    public static UIColor Separator => UIColor.Separator;

    /// <summary>The Wino theme accent when a theme sets one, otherwise the system tint.</summary>
    public static UIColor Accent => AccentOverride ?? UIColor.SystemBlue;

    /// <summary>Set by the theme service. Views read <see cref="Accent"/> when they configure themselves.</summary>
    public static UIColor? AccentOverride
    {
        get => _accentOverride;
        set { _accentOverride = value; AccentChanged?.Invoke(null, EventArgs.Empty); }
    }

    private static UIColor? _accentOverride;

    /// <summary>Raised on the UI thread after the accent changes, so long-lived views can refresh.</summary>
    public static event EventHandler? AccentChanged;

    /// <summary>True while a Wino theme paints the screen (wallpaper or gradient); zones then float over it.</summary>
    public static bool HasBackdrop
    {
        get => _hasBackdrop;
        set { _hasBackdrop = value; BackdropChanged?.Invoke(null, EventArgs.Empty); }
    }

    private static bool _hasBackdrop;

    /// <summary>Raised on the UI thread after the theme backdrop changes.</summary>
    public static event EventHandler? BackdropChanged;

    /// <summary>
    /// The content zone (Windows WinoContentZoneBackgroud) unless the theme sets one: white in light, #1E1E23 in dark
    /// (InboxDark board and the iOS decision record, 15 Oct 2026).
    /// </summary>
    public static UIColor ZoneFill => WinoThemeSurfaces.Dynamic(WinoThemeSurface.Workspace, Dynamic(UIColor.White, Hex(0x1E1E23)));
    public static UIColor ZoneStroke => Dynamic(Hex(0x000000, 0.08), Hex(0xFFFFFF, 0.08));

    /// <summary>A colour that resolves per light or dark appearance.</summary>
    public static UIColor Dynamic(UIColor light, UIColor dark)
        => UIColor.FromDynamicProvider(traits => traits.UserInterfaceStyle == UIUserInterfaceStyle.Dark ? dark : light);

    /// <summary>Grouped rows and cards: the inset grouped surface.</summary>
    public static UIColor GroupFill => UIColor.SecondarySystemGroupedBackground;
    public static UIColor GroupStroke => UIColor.Separator;
    public static UIColor SubtleFill => UIColor.QuaternarySystemFill;

    /// <summary>Selected and pressed row fill (Windows list item selection on a zone).</summary>
    public static UIColor SelectedFill => Dynamic(Hex(0x000000, 0.06), Hex(0xFFFFFF, 0.10));

    /// <summary>Wino status hues used for glyphs and banner tints, never for body text.</summary>
    public static UIColor Flagged => UIColor.SystemRed;
    public static UIColor Caution => UIColor.SystemOrange;
    public static UIColor Success => UIColor.SystemGreen;
    public static UIColor Critical => UIColor.SystemRed;
    public static UIColor Informational => UIColor.SystemBlue;

    /// <summary>The 16-step avatar palette (<see cref="WinoTokens.AvatarPalette"/>).</summary>
    public static readonly UIColor[] AvatarPalette = WinoTokens.AvatarPalette.Select(value => ToColor(WinoRgba.Parse(value))).ToArray();

    public static UIColor Hex(uint rgb, double alpha = 1) => ToColor(WinoRgba.FromRgb(rgb, alpha));

    /// <summary>Parses #RRGGBB or #AARRGGBB; returns null for anything else.</summary>
    public static UIColor? FromHexString(string? value)
        => WinoRgba.FromHexString(value) is { } color ? ToColor(color) : null;

    public static UIColor ToColor(WinoRgba color)
        => UIColor.FromRGBA((nfloat)(color.R / 255.0), (nfloat)(color.G / 255.0), (nfloat)(color.B / 255.0), (nfloat)color.Alpha);

    public static UIColor AvatarColor(string? key) => AvatarPalette[WinoTokens.AvatarPaletteIndex(key)];

    /// <summary>Black or white, whichever reads on the fill (the shared perceived-brightness rule).</summary>
    public static UIColor ReadableText(UIColor fill, UITraitCollection? traits = null)
    {
        var resolved = fill.GetResolvedColor(traits ?? UITraitCollection.CurrentTraitCollection);
        resolved.GetRGBA(out var red, out var green, out var blue, out _);
        return Wino.Core.Domain.Presentation.ReadableTextColor.PrefersDarkText(red, green, blue) ? UIColor.Black : UIColor.White;
    }

    public static UILabel Label(string? text, UIFont? font = null, UIColor? color = null, int maximumLines = 1)
    {
        var label = new UILabel
        {
            Text = text ?? string.Empty,
            Font = font ?? Body,
            TextColor = color ?? PrimaryText,
            Lines = maximumLines,
            LineBreakMode = maximumLines == 1 ? UILineBreakMode.TailTruncation : UILineBreakMode.WordWrap,
            AdjustsFontForContentSizeCategory = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        if (maximumLines != 1) label.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        return label;
    }
}

/// <summary>Accessibility setters with the same shape as the AppKit <c>WinoAccessibility</c>.</summary>
public static class WinoAccessibility
{
    public static T Label<T>(T view, string? label) where T : UIView
    {
        view.AccessibilityLabel = label;
        return view;
    }

    /// <summary>The AppKit help text maps to the UIKit accessibility hint.</summary>
    public static T Help<T>(T view, string? help) where T : UIView
    {
        view.AccessibilityHint = help;
        return view;
    }

    /// <summary>Marks a view as one accessible element with the given traits.</summary>
    public static T Element<T>(T view, UIAccessibilityTrait traits, string? label = null) where T : UIView
    {
        view.IsAccessibilityElement = true;
        view.AccessibilityTraits = traits;
        if (label is not null) view.AccessibilityLabel = label;
        return view;
    }
}

/// <summary>Trait-change registrations shared by the Wino views (iOS 17 registration API).</summary>
public static class WinoTraits
{
    /// <summary>The traits that change resolved colours and the colorful icon palette.</summary>
    public static Type[] Appearance { get; } = [typeof(UITraitUserInterfaceStyle), typeof(UITraitAccessibilityContrast)];

    /// <summary>True when the traits (or the current traits) are dark.</summary>
    public static bool IsDark(UITraitCollection? traits)
        => (traits ?? UITraitCollection.CurrentTraitCollection).UserInterfaceStyle == UIUserInterfaceStyle.Dark;

    /// <summary>
    /// Runs <paramref name="changed"/> on the view after a light, dark or contrast change. The callback receives the
    /// view, so it never needs to capture it.
    /// </summary>
    public static IUITraitChangeRegistration OnAppearanceChanged<T>(T view, Action<T> changed) where T : UIView
        => view.RegisterForTraitChanges(Appearance, (environment, _) =>
        {
            if (environment is T target) changed(target);
        });
}
