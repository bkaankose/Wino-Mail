using System;
using System.Collections.Generic;
using System.Drawing;
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

        var color = ColorTranslator.FromHtml(normalizedColor);
        var luminance = ((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B)) / 255d;
        return luminance > 0.6 ? "#111111" : "#FFFFFF";
    }

    public static string ToHexString(this Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static string ToRgbString(this Color c) => $"RGB({c.R}, {c.G}, {c.B})";
    private static string AdjustColor(string hexColor, int cycle)
    {
        var color = ColorTranslator.FromHtml(hexColor);
        var factor = Math.Max(0.55, 1.0 - (cycle * 0.08));

        var adjusted = Color.FromArgb(
            (int)Math.Clamp(color.R * factor, 0, 255),
            (int)Math.Clamp(color.G * factor, 0, 255),
            (int)Math.Clamp(color.B * factor, 0, 255));

        return adjusted.ToHexString();
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
}
