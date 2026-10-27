using System;
using System.Collections.Generic;
using Windows.UI;
using Wino.Core.Domain.Presentation;

namespace Wino.Helpers;

/// <summary>The calendar-colour derived colours of a tinted event tile for one theme.</summary>
/// <param name="Accent">The calendar colour: bar, hover and selection tint.</param>
/// <param name="Wash">The tile background.</param>
/// <param name="PastWash">The weaker background of past events and unanswered invitations.</param>
/// <param name="Text">The calendar colour adjusted to 4.5:1 against the hover wash, for the time line and glyphs.</param>
/// <param name="TentativeOutline">The hairline of tentative events.</param>
/// <param name="FreeOutline">The outline of free events, which have no wash.</param>
/// <param name="NeutralOutline">The outline of cancelled events.</param>
/// <param name="Ghost">The background of a multi-day event's ghost in the timed grid.</param>
internal readonly record struct CalendarTileColorSet(
    Color Accent,
    Color Wash,
    Color PastWash,
    Color Text,
    Color TentativeOutline,
    Color FreeOutline,
    Color NeutralOutline,
    Color Ghost);

/// <summary>
/// Derives the tinted tile colours from a user-chosen calendar colour. Washes are pre-mixed with an
/// opaque grid colour rather than blended over the page, so image and acrylic themes do not show
/// through and muddy the tint.
/// </summary>
internal static class CalendarTileColors
{
    private const double MinimumTextContrast = 4.5;
    private const double LightnessStep = 0.015;
    private const int MaximumLightnessSteps = 60;

    private static readonly Color LightGridColor = Color.FromArgb(255, 0xFB, 0xFB, 0xFB);
    private static readonly Color DarkGridColor = Color.FromArgb(255, 0x20, 0x20, 0x20);
    private static readonly Color FallbackColor = Color.FromArgb(255, 0x0F, 0x6C, 0xBD);

    private static readonly Dictionary<(string Hex, bool IsDark), CalendarTileColorSet> Cache = new();
    private static readonly object CacheLock = new();

    public static CalendarTileColorSet Get(string? colorHex, bool isDarkTheme)
    {
        var key = (colorHex?.Trim().ToUpperInvariant() ?? string.Empty, isDarkTheme);

        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }

        var accent = ReadableTextColor.TryParseRgb(colorHex, out var red, out var green, out var blue)
            ? Color.FromArgb(255, (byte)red, (byte)green, (byte)blue)
            : FallbackColor;
        var grid = isDarkTheme ? DarkGridColor : LightGridColor;
        var hoverWash = Mix(accent, grid, isDarkTheme ? 0.36 : 0.26);

        var colors = new CalendarTileColorSet(
            accent,
            Mix(accent, grid, isDarkTheme ? 0.26 : 0.16),
            Mix(accent, grid, isDarkTheme ? 0.10 : 0.07),
            GetAccessibleTextColor(accent, hoverWash, darken: !isDarkTheme),
            WithAlpha(accent, 0.45),
            WithAlpha(accent, 0.55),
            isDarkTheme ? Color.FromArgb(41, 255, 255, 255) : Color.FromArgb(36, 0, 0, 0),
            WithAlpha(accent, 0.06));

        lock (CacheLock)
        {
            Cache[key] = colors;
        }

        return colors;
    }

    /// <summary>
    /// Keeps the hue and saturation of <paramref name="color"/> and steps its lightness down (or up)
    /// until it reaches 4.5:1 against <paramref name="background"/>, or lightness runs out.
    /// </summary>
    public static Color GetAccessibleTextColor(Color color, Color background, bool darken)
    {
        var (hue, saturation, lightness) = ToHsl(color);
        var candidate = color;

        for (var step = 0; step < MaximumLightnessSteps && GetContrastRatio(candidate, background) < MinimumTextContrast; step++)
        {
            lightness += darken ? -LightnessStep : LightnessStep;
            if (lightness is < 0d or > 1d)
            {
                candidate = FromHsl(hue, saturation, Math.Clamp(lightness, 0d, 1d));
                break;
            }

            candidate = FromHsl(hue, saturation, lightness);
        }

        return candidate;
    }

    public static double GetContrastRatio(Color first, Color second)
    {
        var firstLuminance = ReadableTextColor.RelativeLuminance(first.R, first.G, first.B);
        var secondLuminance = ReadableTextColor.RelativeLuminance(second.R, second.G, second.B);

        return (Math.Max(firstLuminance, secondLuminance) + 0.05) / (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static Color Mix(Color color, Color background, double amount)
        => Color.FromArgb(
            255,
            MixChannel(color.R, background.R, amount),
            MixChannel(color.G, background.G, amount),
            MixChannel(color.B, background.B, amount));

    private static byte MixChannel(byte color, byte background, double amount)
        => (byte)Math.Round((color * amount) + (background * (1d - amount)));

    private static Color WithAlpha(Color color, double alpha)
        => Color.FromArgb((byte)Math.Round(alpha * 255d), color.R, color.G, color.B);

    private static (double Hue, double Saturation, double Lightness) ToHsl(Color color)
    {
        var red = color.R / 255d;
        var green = color.G / 255d;
        var blue = color.B / 255d;
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var lightness = (max + min) / 2d;

        if (max == min) return (0d, 0d, lightness);

        var delta = max - min;
        var saturation = lightness > 0.5 ? delta / (2d - max - min) : delta / (max + min);
        var hue = max == red
            ? ((green - blue) / delta) + (green < blue ? 6d : 0d)
            : max == green
                ? ((blue - red) / delta) + 2d
                : ((red - green) / delta) + 4d;

        return (hue / 6d, saturation, lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        if (saturation == 0d)
        {
            var gray = ToByte(lightness);
            return Color.FromArgb(255, gray, gray, gray);
        }

        var q = lightness < 0.5 ? lightness * (1d + saturation) : lightness + saturation - (lightness * saturation);
        var p = (2d * lightness) - q;

        return Color.FromArgb(
            255,
            ToByte(HueToChannel(p, q, hue + (1d / 3d))),
            ToByte(HueToChannel(p, q, hue)),
            ToByte(HueToChannel(p, q, hue - (1d / 3d))));
    }

    private static double HueToChannel(double p, double q, double t)
    {
        if (t < 0d) t += 1d;
        if (t > 1d) t -= 1d;
        if (t < 1d / 6d) return p + ((q - p) * 6d * t);
        if (t < 1d / 2d) return q;
        if (t < 2d / 3d) return p + ((q - p) * ((2d / 3d) - t) * 6d);
        return p;
    }

    private static byte ToByte(double channel) => (byte)Math.Round(Math.Clamp(channel, 0d, 1d) * 255d);
}
