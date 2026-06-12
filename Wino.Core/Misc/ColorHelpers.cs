using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wino.Core.Domain.Misc;

namespace Wino.Core.Misc;

public static class ColorHelpers
{
    public static IReadOnlyList<string> GetFlatColorPalette() => CalendarColorPalette.GetColors();

    public static string GenerateFlatColorHex() => GetDistinctFlatColorHex(Array.Empty<string>());

    public static string GetDistinctFlatColorHex(IEnumerable<string> usedColors, string preferredColor = null)
    {
        var palette = CalendarColorPalette.GetColors();
        var normalizedUsedColors = usedColors?
            .Select(NormalizeHexColor)
            .Where(color => !string.IsNullOrWhiteSpace(color))
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (TryNormalizeHexColor(preferredColor, out var normalizedPreferred) &&
            palette.Contains(normalizedPreferred, StringComparer.OrdinalIgnoreCase) &&
            !normalizedUsedColors.Contains(normalizedPreferred))
        {
            return normalizedPreferred;
        }

        var distinctColor = CalendarColorPalette.GetDistinctColor(usedColors);
        if (palette.Contains(distinctColor))
        {
            return distinctColor;
        }

        var candidate = AdjustColor(palette[0], 1);

        return candidate;
    }

    public static string GetReadableTextColorHex(string backgroundColor)
    {
        if (!TryNormalizeHexColor(backgroundColor, out var normalizedColor))
        {
            return "#FFFFFF";
        }

        var (red, green, blue) = ParseRgb(normalizedColor);
        var luminance = ((0.299 * red) + (0.587 * green) + (0.114 * blue)) / 255d;
        return luminance > 0.6 ? "#111111" : "#FFFFFF";
    }

    private static string AdjustColor(string hexColor, int cycle)
    {
        var (red, green, blue) = ParseRgb(hexColor);
        var factor = Math.Max(0.55, 1.0 - (cycle * 0.08));

        var adjustedRed = (int)Math.Clamp(red * factor, 0, 255);
        var adjustedGreen = (int)Math.Clamp(green * factor, 0, 255);
        var adjustedBlue = (int)Math.Clamp(blue * factor, 0, 255);

        return $"#{adjustedRed:X2}{adjustedGreen:X2}{adjustedBlue:X2}";
    }

    private static bool TryNormalizeHexColor(string value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var color = value.Trim();
        if (color.StartsWith('#'))
        {
            color = color[1..];
        }

        if (color.Length != 6)
        {
            return false;
        }

        if (!int.TryParse(color, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        normalized = $"#{color.ToUpperInvariant()}";
        return true;
    }

    private static string NormalizeHexColor(string value)
        => TryNormalizeHexColor(value, out var normalized) ? normalized : string.Empty;

    private static (int Red, int Green, int Blue) ParseRgb(string hexColor)
    {
        var normalized = NormalizeHexColor(hexColor);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return (255, 255, 255);
        }

        return (
            Convert.ToInt32(normalized.Substring(1, 2), 16),
            Convert.ToInt32(normalized.Substring(3, 2), 16),
            Convert.ToInt32(normalized.Substring(5, 2), 16));
    }
}
