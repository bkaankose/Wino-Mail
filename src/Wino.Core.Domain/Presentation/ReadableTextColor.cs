#nullable enable
using System;
using System.Globalization;

namespace Wino.Core.Domain.Presentation;

/// <summary>
/// Chooses black or white text for a coloured fill. Two rules exist and are kept apart on purpose:
/// the perceived-brightness rule used for calendar tiles and category chips, and the WCAG
/// relative-luminance rule used by the Windows readable-text helper.
/// </summary>
public static class ReadableTextColor
{
    /// <summary>
    /// Perceived brightness (0.299 R + 0.587 G + 0.114 B, components 0 to 1) above 0.6 reads better
    /// with dark text. Calendar tiles and category chips use this rule.
    /// </summary>
    public static bool PrefersDarkText(double red, double green, double blue)
        => 0.299 * red + 0.587 * green + 0.114 * blue > 0.6;

    /// <summary>WCAG relative luminance of an sRGB colour with 0 to 255 components.</summary>
    public static double RelativeLuminance(int red, int green, int blue)
        => 0.2126 * Linearize(red) + 0.7152 * Linearize(green) + 0.0722 * Linearize(blue);

    /// <summary>Relative luminance above 0.5 reads better with dark text (the Windows readable-text rule).</summary>
    public static bool PrefersDarkTextByLuminance(int red, int green, int blue)
        => RelativeLuminance(red, green, blue) > 0.5;

    /// <summary>Parses #RRGGBB (the leading # is optional) into 0 to 255 components.</summary>
    public static bool TryParseRgb(string? hex, out int red, out int green, out int blue)
    {
        red = green = blue = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;

        var text = hex.Trim().TrimStart('#');
        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return false;

        red = (value >> 16) & 0xFF;
        green = (value >> 8) & 0xFF;
        blue = value & 0xFF;
        return true;
    }

    private static double Linearize(int component)
    {
        var srgb = Math.Clamp(component, 0, 255) / 255.0;
        return srgb <= 0.03928 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
    }
}
