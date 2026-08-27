using System;
using System.Collections.Generic;
using System.Globalization;

namespace Wino.Core.Domain.Misc;

/// <summary>Shared flat colors for calendars, task lists, and other account-owned collections.</summary>
public static class ColorPalette
{
    private static readonly string[] FlatUiColorPalette =
    [
        "#E53935", "#D81B60", "#C2185B", "#AD1457", "#8E24AA", "#7B1FA2", "#6A1B9A", "#5E35B1", "#512DA8", "#4527A0",
        "#3949AB", "#303F9F", "#283593", "#1E88E5", "#1976D2", "#1565C0", "#039BE5", "#0288D1", "#0277BD", "#00ACC1",
        "#0097A7", "#00838F", "#00897B", "#00796B", "#00695C", "#43A047", "#388E3C", "#2E7D32", "#7CB342", "#689F38",
        "#558B2F", "#9CCC65", "#8BC34A", "#AED581", "#C0CA33", "#AFB42B", "#9E9D24", "#D4E157", "#CDDC39", "#FDD835",
        "#FBC02D", "#F9A825", "#FFB300", "#FFA000", "#FF8F00", "#FB8C00", "#F57C00", "#EF6C00", "#F4511E", "#E64A19",
        "#D84315", "#FF7043", "#FF8A65", "#FFAB91", "#6D4C41", "#5D4037", "#4E342E", "#8D6E63", "#795548", "#A1887F",
        "#546E7A", "#455A64", "#37474F", "#607D8B", "#78909C", "#90A4AE", "#757575", "#616161", "#424242", "#9E9E9E",
        "#BDBDBD", "#EC407A", "#F06292", "#F48FB1", "#BA68C8", "#CE93D8", "#9575CD", "#B39DDB", "#7986CB", "#9FA8DA",
        "#64B5F6", "#90CAF9", "#4FC3F7", "#81D4FA", "#4DD0E1", "#80DEEA", "#4DB6AC", "#80CBC4", "#81C784", "#A5D6A7",
        "#C5E1A5", "#E6EE9C", "#FFF176", "#FFD54F", "#FFCC80", "#FFB74D", "#FFAB40", "#FF9E80", "#BCAAA4", "#A1887F",
        "#B71C1C", "#880E4F", "#4A148C", "#311B92", "#1A237E", "#0D47A1", "#01579B", "#006064", "#004D40", "#1B5E20",
        "#33691E", "#827717", "#F57F17", "#FF6F00", "#E65100", "#BF360C", "#3E2723", "#263238", "#FF1744", "#F50057",
        "#D500F9", "#651FFF", "#3D5AFE", "#2979FF", "#00B0FF", "#00E5FF", "#1DE9B6", "#00E676"
    ];

    public static IReadOnlyList<string> GetColors() => FlatUiColorPalette;

    public static string GetDistinctColor(IEnumerable<string> usedColors)
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

        return FlatUiColorPalette[0];
    }

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
}
