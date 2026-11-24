using System;
using System.Globalization;
using Wino.Renderer.Primitives;

namespace Wino.Mail.Rendering;

/// <summary>
/// The color values the editor command bars send: <c>#rrggbb</c>, <c>#aarrggbb</c>, <c>rgb()</c>, <c>rgba()</c> and a
/// few names, with the values the Windows command bar's swatches use. Transparent when the value is none of them.
/// </summary>
public static class EditorColors
{
    public static Rgba Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Rgba.Transparent;

        var normalized = value.Trim();
        if (string.Equals(normalized, "transparent", StringComparison.OrdinalIgnoreCase))
            return Rgba.Transparent;

        if (TryParseRgb(normalized, out var rgb))
            return rgb;

        if (TryParseNamed(normalized) is { } named)
            return named;

        var hex = normalized.TrimStart('#');
        if (hex.Length == 6)
            hex = $"FF{hex}";

        if (hex.Length != 8 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            return Rgba.Transparent;

        return new Rgba((byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF), (byte)((argb >> 24) & 0xFF));
    }

    private static bool TryParseRgb(string value, out Rgba color)
    {
        color = Rgba.Transparent;
        var isRgba = value.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase);
        var isRgb = value.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase);
        if (!isRgb && !isRgba)
            return false;

        var start = value.IndexOf('(');
        var end = value.LastIndexOf(')');
        if (start < 0 || end <= start)
            return false;

        var segments = value[(start + 1)..end].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if ((isRgb && segments.Length != 3) || (isRgba && segments.Length != 4))
            return false;

        if (!byte.TryParse(segments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var red) ||
            !byte.TryParse(segments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var green) ||
            !byte.TryParse(segments[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var blue))
            return false;

        byte alpha = 255;
        if (isRgba)
        {
            if (!double.TryParse(segments[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var alphaValue))
                return false;

            alpha = alphaValue <= 1d
                ? (byte)Math.Clamp(Math.Round(alphaValue * 255d), 0d, 255d)
                : (byte)Math.Clamp(Math.Round(alphaValue), 0d, 255d);
        }

        color = new Rgba(red, green, blue, alpha);
        return true;
    }

    private static Rgba? TryParseNamed(string value) => value.ToLowerInvariant() switch
    {
        "black" => new Rgba(0, 0, 0, 255),
        "white" => new Rgba(255, 255, 255, 255),
        "gray" or "grey" => new Rgba(128, 128, 128, 255),
        "red" => new Rgba(255, 0, 0, 255),
        "orange" => new Rgba(255, 165, 0, 255),
        "yellow" => new Rgba(255, 255, 0, 255),
        "green" => new Rgba(0, 128, 0, 255),
        "blue" => new Rgba(0, 0, 255, 255),
        "purple" => new Rgba(128, 0, 128, 255),
        "pink" => new Rgba(255, 192, 203, 255),
        _ => null
    };
}
