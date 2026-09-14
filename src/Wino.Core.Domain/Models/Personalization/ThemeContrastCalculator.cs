using System;
using System.Globalization;

namespace Wino.Core.Domain.Models.Personalization;

/// <summary>
/// Reports the contrast of Wino body text on a custom theme surface.
/// Custom surfaces are usually translucent, so a surface is composited over the base
/// surface, and the base surface over the average wallpaper color, before measuring.
/// </summary>
public static class ThemeContrastCalculator
{
    private const double MinimumBodyTextContrast = 4.5;

    private static readonly Rgb DarkThemeText = new(0xE4, 0xE4, 0xE4);
    private static readonly Rgb LightThemeText = new(0x1A, 0x1A, 0x1A);

    /// <summary>
    /// Returns the WCAG contrast ratio of body text on <paramref name="surfaceColor"/>,
    /// or null when any input cannot be measured.
    /// </summary>
    public static double? TryGetBodyTextContrast(string? surfaceColor, string? baseSurfaceColor, string? wallpaperAverageColor)
    {
        if (!TryParse(surfaceColor, out var surface) ||
            !TryParse(baseSurfaceColor, out var baseSurface) ||
            !TryParse(wallpaperAverageColor, out var wallpaper))
            return null;

        var ground = Composite(baseSurface, Opaque(wallpaper));
        var resolved = Composite(surface, ground);
        var text = Luminance(resolved) < 0.42 ? DarkThemeText : LightThemeText;

        return Ratio(text, resolved);
    }

    public static bool IsSufficient(double ratio) => ratio >= MinimumBodyTextContrast;

    private static bool TryParse(string? value, out Argb color)
    {
        color = default;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var hex = value.Trim().TrimStart('#');

        if (hex.Length == 6)
            hex = "FF" + hex;

        if (hex.Length != 8)
            return false;

        if (!byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a) ||
            !byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(hex.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return false;

        color = new Argb(a, r, g, b);
        return true;
    }

    private static Rgb Opaque(Argb color) => new(color.R, color.G, color.B);

    private static Rgb Composite(Argb foreground, Rgb background)
    {
        var alpha = foreground.A / 255d;

        return new Rgb(
            foreground.R * alpha + background.R * (1 - alpha),
            foreground.G * alpha + background.G * (1 - alpha),
            foreground.B * alpha + background.B * (1 - alpha));
    }

    private static double Ratio(Rgb first, Rgb second)
    {
        var a = Luminance(first);
        var b = Luminance(second);

        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(Rgb color)
        => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);

    private static double Channel(double value)
    {
        var normalized = value / 255d;
        return normalized <= 0.03928 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
    }

    private readonly record struct Argb(byte A, byte R, byte G, byte B);

    private readonly record struct Rgb(double R, double G, double B);
}
