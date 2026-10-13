using System.Globalization;

namespace Wino.Presentation;

/// <summary>An sRGB colour with 8-bit channels and a 0-1 alpha, independent of any UI framework.</summary>
public readonly record struct WinoRgba(byte R, byte G, byte B, double Alpha = 1)
{
    /// <summary>From a 0xRRGGBB value.</summary>
    public static WinoRgba FromRgb(uint rgb, double alpha = 1)
        => new((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF), alpha);

    /// <summary>Parses #RRGGBB or #AARRGGBB; returns null for anything else.</summary>
    public static WinoRgba? FromHexString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim().TrimStart('#');
        if (!uint.TryParse(text, NumberStyles.HexNumber, null, out var parsed)) return null;
        return text.Length switch
        {
            6 => FromRgb(parsed),
            8 => FromRgb(parsed & 0xFFFFFF, ((parsed >> 24) & 0xFF) / 255.0),
            _ => null
        };
    }

    /// <summary>Parses #RRGGBB or #AARRGGBB and throws <see cref="FormatException"/> for anything else.</summary>
    public static WinoRgba Parse(string value)
        => FromHexString(value) ?? throw new FormatException($"'{value}' is not a #RRGGBB or #AARRGGBB colour.");
}
