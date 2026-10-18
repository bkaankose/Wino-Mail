using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using AndroidX.Core.Content;
using Wino.Mail.Controls.Core;

namespace Wino.Presentation.Android;

/// <summary>
/// Wino colours for Android views (UIKit <c>WinoStyle</c> colours): the token resources in values/colors.xml and
/// values-night/colors.xml resolved for a context's appearance, the runtime accent of the selected theme, the avatar
/// palette and conversions from the platform-neutral <see cref="WinoRgba"/>.
/// </summary>
public static class WinoColors
{
    private static Color? _accentOverride;

    /// <summary>The selected theme's accent; null uses the resource accent (the device's Material You accent on Android 12 and later, else Wino blue).</summary>
    public static Color? AccentOverride
    {
        get => _accentOverride;
        set
        {
            if (_accentOverride == value) return;
            _accentOverride = value;
            AccentChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Raised on the UI thread after the accent changes so long-lived views can recolour.</summary>
    public static event EventHandler? AccentChanged;

    public static bool IsDark(Context context)
        => (context.Resources?.Configuration?.UiMode & UiMode.NightMask) == UiMode.NightYes;

    public static Color FromResource(Context context, int colorResource) => new(ContextCompat.GetColor(context, colorResource));

    public static Color Accent(Context context) => _accentOverride ?? FromResource(context, Resource.Color.wino_accent);

    /// <summary>The accent for text on the zone (4.5:1): the resource value unless a custom accent is set.</summary>
    public static Color AccentText(Context context) => _accentOverride ?? FromResource(context, Resource.Color.wino_accent_text);

    public static Color OnAccent(Context context) => _accentOverride is { } accent ? ReadableText(accent) : FromResource(context, Resource.Color.wino_on_accent);
    public static Color PrimaryText(Context context) => FromResource(context, Resource.Color.wino_text_primary);
    public static Color SecondaryText(Context context) => FromResource(context, Resource.Color.wino_text_secondary);
    public static Color TertiaryText(Context context) => FromResource(context, Resource.Color.wino_text_tertiary);
    public static Color ZoneFill(Context context) => WinoThemeSurfaces.Resolve(WinoThemeSurface.Workspace, IsDark(context)) ?? FromResource(context, Resource.Color.wino_zone);
    public static Color ZoneStroke(Context context) => FromResource(context, Resource.Color.wino_zone_stroke);
    public static Color CardFill(Context context) => FromResource(context, Resource.Color.wino_card);

    /// <summary>The list under settings sections: the theme's Workspace surface when it has one, else the settings list token.</summary>
    public static Color SettingsListFill(Context context) => WinoThemeSurfaces.Resolve(WinoThemeSurface.Workspace, IsDark(context)) ?? FromResource(context, Resource.Color.wino_settings_list_fill);

    /// <summary>A settings section card (white in light, a raised surface container in dark).</summary>
    public static Color SettingsGroupFill(Context context) => FromResource(context, Resource.Color.wino_settings_group_fill);
    public static Color CardStroke(Context context) => FromResource(context, Resource.Color.wino_card_stroke);
    public static Color Divider(Context context) => FromResource(context, Resource.Color.wino_divider);
    public static Color Outline(Context context) => FromResource(context, Resource.Color.wino_outline);
    public static Color SubtleFill(Context context) => FromResource(context, Resource.Color.wino_subtle_fill);
    public static Color BarTint(Context context) => FromResource(context, Resource.Color.wino_bar_tint);
    public static Color Critical(Context context) => FromResource(context, Resource.Color.wino_critical);
    public static Color Success(Context context) => FromResource(context, Resource.Color.wino_success);
    public static Color Caution(Context context) => FromResource(context, Resource.Color.wino_caution);
    public static Color Flagged(Context context) => FromResource(context, Resource.Color.wino_flagged);

    /// <summary>
    /// Material 3 secondary container (navigation indicators, the checked segment, the selected drawer item): the resource
    /// token, or a theme accent over the zone at <c>wino_m3_secondary_container_amount</c>.
    /// </summary>
    public static Color SecondaryContainer(Context context)
        => _accentOverride is { } accent ? Blend(accent, ZoneFill(context), Fraction(context, Resource.Fraction.wino_m3_secondary_container_amount)) : FromResource(context, Resource.Color.wino_secondary_container);

    /// <summary>Material 3 primary container (the tonal FAB): the resource token, or a theme accent over the zone.</summary>
    public static Color PrimaryContainer(Context context)
        => _accentOverride is { } accent ? Blend(accent, ZoneFill(context), Fraction(context, Resource.Fraction.wino_m3_primary_container_amount)) : FromResource(context, Resource.Color.wino_primary_container);

    /// <summary>Content on <see cref="PrimaryContainer"/> (the FAB glyph).</summary>
    public static Color OnPrimaryContainer(Context context) => AccentText(context);

    /// <summary>A fraction resource (0 to 1).</summary>
    public static float Fraction(Context context, int fractionResource) => context.Resources?.GetFraction(fractionResource, 1, 1) ?? 0f;

    /// <summary>The calendar's today column: the theme accent at 4% (8% in dark mode).</summary>
    public static Color CalendarTodayColumn(Context context) => WithAlpha(Accent(context), IsDark(context) ? 0.08 : 0.04);

    /// <summary>The mini calendar's selected range band: the theme accent at 14% (20% in dark mode).</summary>
    public static Color CalendarRangeBand(Context context) => WithAlpha(Accent(context), IsDark(context) ? 0.20 : 0.14);

    /// <summary>Selected rows: the accent at 10% (canvas Components, selected mail row).</summary>
    public static Color SelectedFill(Context context) => WithAlpha(Accent(context), 0.10);

    /// <summary>The mail list group strip (Today, Yesterday): accent tint 30% in light, white 7% in dark, or the theme's MailListHeader.</summary>
    public static Color GroupStripFill(Context context)
    {
        bool dark = IsDark(context);
        if (WinoThemeSurfaces.Resolve(WinoThemeSurface.MailListHeader, dark) is { } custom) return custom;
        return dark ? new Color(255, 255, 255, 18) : Blend(Accent(context), Color.White, 0.30);
    }

    public static Color ToColor(WinoRgba color) => new(color.R, color.G, color.B, (byte)Math.Round(Math.Clamp(color.Alpha, 0, 1) * 255));

    /// <summary>#RRGGBB or #AARRGGBB; null for anything else.</summary>
    public static Color? FromHexString(string? value) => WinoRgba.FromHexString(value) is { } color ? ToColor(color) : null;

    public static Color Hex(uint rgb, double alpha = 1) => ToColor(WinoRgba.FromRgb(rgb, alpha));

    /// <summary>The stable avatar colour for an address or name (shared <see cref="ContactPictureAppearance"/> palette).</summary>
    public static Color AvatarColor(string? key) => Hex(ContactPictureAppearance.PaletteColor(key));

    public static Color WithAlpha(Color color, double alpha) => new(color.R, color.G, color.B, (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255));

    /// <summary><paramref name="top"/> at <paramref name="amount"/> over an opaque <paramref name="bottom"/>.</summary>
    public static Color Blend(Color top, Color bottom, double amount)
    {
        byte Mix(byte a, byte b) => (byte)Math.Round(a * amount + b * (1 - amount));
        return new Color(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B), (byte)255);
    }

    /// <summary>Black or white, whichever reads on the given fill (Windows GetReadableTextColor).</summary>
    public static Color ReadableText(Color fill)
    {
        double luminance = (0.299 * fill.R + 0.587 * fill.G + 0.114 * fill.B) / 255;
        return luminance > 0.6 ? new Color(0x1B, 0x1B, 0x1B) : Color.White;
    }
}
