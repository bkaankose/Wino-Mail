using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Wino.Core.Domain.Misc;

namespace Wino.Core.Misc;

public static class ColorHelpers
{
    public static IReadOnlyList<string> GetFlatColorPalette() => CalendarColorPalette.GetColors();

    public static string GenerateFlatColorHex() => GetDistinctFlatColorHex(Array.Empty<string>());

    public static string GetDistinctFlatColorHex(IEnumerable<string> usedColors)
    {
        var palette = CalendarColorPalette.GetColors();
        var distinctColor = CalendarColorPalette.GetDistinctColor(usedColors);
        if (palette.Contains(distinctColor))
        {
            return distinctColor;
        }

        var candidate = AdjustColor(palette[0], 1);

        return candidate;
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
}
