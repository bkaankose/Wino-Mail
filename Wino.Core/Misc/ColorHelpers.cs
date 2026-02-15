using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;

namespace Wino.Core.Misc;

public static class ColorHelpers
{
    private static readonly string[] FlatUiColorPalette =
    [
        "#B91C1C", "#C2410C", "#B45309", "#A16207", "#4D7C0F", "#15803D", "#047857", "#0F766E", "#0E7490", "#0369A1",
        "#1D4ED8", "#4338CA", "#6D28D9", "#7E22CE", "#A21CAF", "#BE185D", "#E11D48", "#DC2626", "#EA580C", "#D97706",
        "#CA8A04", "#65A30D", "#16A34A", "#059669", "#0D9488", "#0891B2", "#0284C7", "#2563EB", "#4F46E5", "#7C3AED",
        "#9333EA", "#C026D3", "#DB2777", "#F43F5E", "#EF4444", "#F97316", "#F59E0B", "#EAB308", "#84CC16", "#22C55E",
        "#10B981", "#14B8A6", "#06B6D4", "#0EA5E9", "#3B82F6", "#6366F1", "#8B5CF6", "#A855F7", "#D946EF", "#EC4899",
        "#FB7185", "#F87171", "#FB923C", "#FBBF24", "#FACC15", "#A3E635", "#4ADE80", "#34D399", "#2DD4BF", "#22D3EE",
        "#38BDF8", "#60A5FA", "#818CF8", "#A78BFA", "#C084FC", "#E879F9", "#F472B6", "#FDA4AF", "#FCA5A5", "#FDBA74",
        "#FCD34D", "#FDE047", "#BEF264", "#86EFAC", "#6EE7B7", "#5EEAD4", "#67E8F9", "#7DD3FC", "#93C5FD", "#A5B4FC",
        "#C4B5FD", "#D8B4FE", "#F0ABFC", "#F9A8D4", "#A16207", "#9A3412", "#7C2D12", "#6F1D1B", "#7F1D1D", "#881337",
        "#831843", "#701A75", "#581C87", "#312E81", "#1E3A8A", "#1D4ED8", "#155E75", "#134E4A", "#14532D", "#3F6212",
        "#365314", "#3F3F46", "#52525B", "#57534E", "#44403C", "#78716C", "#6B7280", "#4B5563", "#374151", "#1F2937",
        "#A16207", "#B45309", "#C2410C", "#9F1239", "#BE123C", "#C026D3", "#7E22CE", "#6D28D9", "#4338CA", "#1D4ED8"
    ];

    public static IReadOnlyList<string> GetFlatColorPalette() => FlatUiColorPalette;

    public static string GenerateFlatColorHex() => GetDistinctFlatColorHex(Array.Empty<string>());

    public static string GetDistinctFlatColorHex(IEnumerable<string> usedColors)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (usedColors != null)
        {
            foreach (var color in usedColors)
            {
                if (TryNormalizeHexColor(color, out var normalized))
                {
                    used.Add(normalized);
                }
            }
        }

        foreach (var color in FlatUiColorPalette)
        {
            if (!used.Contains(color))
            {
                return color;
            }
        }

        var attempt = 0;
        while (attempt < 500)
        {
            var baseColor = FlatUiColorPalette[attempt % FlatUiColorPalette.Length];
            var cycle = (attempt / FlatUiColorPalette.Length) + 1;
            var candidate = AdjustColor(baseColor, cycle);

            if (!used.Contains(candidate))
            {
                return candidate;
            }

            attempt++;
        }

        return "#5C7A8A";
    }

    public static string ToHexString(this Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static string ToRgbString(this Color c) => $"RGB({c.R}, {c.G}, {c.B})";

    private static bool TryNormalizeHexColor(string value, out string normalized)
    {
        normalized = null;
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
}
