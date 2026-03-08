using System;
using System.Collections.Generic;
using System.Globalization;

namespace Wino.Core.Domain.Misc;

public static class CalendarColorPalette
{
    private static readonly string[] FlatUiColorPalette =
    [
        "#E53935", "#D81B60", "#8E24AA", "#5E35B1", "#3949AB", "#1E88E5", "#039BE5", "#00ACC1", "#00897B", "#43A047",
        "#7CB342", "#C0CA33", "#FDD835", "#FFB300", "#FB8C00", "#F4511E", "#6D4C41", "#757575", "#546E7A", "#C62828",
        "#AD1457", "#6A1B9A", "#4527A0", "#283593", "#1565C0", "#0277BD", "#00838F", "#00695C", "#2E7D32", "#558B2F",
        "#9E9D24", "#F9A825", "#FF8F00", "#EF6C00", "#D84315", "#4E342E", "#616161", "#455A64", "#EF5350", "#EC407A",
        "#AB47BC", "#7E57C2", "#5C6BC0", "#42A5F5", "#29B6F6", "#26C6DA", "#26A69A", "#66BB6A", "#9CCC65", "#D4E157",
        "#FFEE58", "#FFCA28", "#FFA726", "#FF7043", "#8D6E63", "#BDBDBD", "#78909C", "#F06292", "#BA68C8", "#9575CD"
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
