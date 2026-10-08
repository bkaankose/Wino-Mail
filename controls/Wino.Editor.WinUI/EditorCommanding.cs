using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using System.Globalization;
namespace Wino.Editor;
public sealed record class EditorColorOption(string Name, string Value)
{
    public SolidColorBrush Brush => new(ParseColorValue(Value));

    /// <summary>
    /// False for the "Automatic" / "No color" entries that clear the color instead of applying one.
    /// </summary>
    public bool HasColor => !string.IsNullOrWhiteSpace(Value);

    public static Color ParseColorValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Colors.Transparent;
        }

        var normalizedValue = value.Trim();

        if (string.Equals(normalizedValue, "transparent", StringComparison.OrdinalIgnoreCase))
        {
            return Colors.Transparent;
        }

        if (TryParseRgbColor(normalizedValue, out var rgbColor))
        {
            return rgbColor;
        }

        if (TryParseNamedColor(normalizedValue, out var namedColor))
        {
            return namedColor;
        }

        var hex = normalizedValue.TrimStart('#');
        if (hex.Length == 6)
        {
            hex = $"FF{hex}";
        }

        if (hex.Length != 8 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var argb))
        {
            return Colors.Transparent;
        }

        return Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));
    }

    private static bool TryParseRgbColor(string value, out Color color)
    {
        color = Colors.Transparent;

        var isRgba = value.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase);
        var isRgb = value.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase);
        if (!isRgb && !isRgba)
        {
            return false;
        }

        var startIndex = value.IndexOf('(');
        var endIndex = value.LastIndexOf(')');
        if (startIndex < 0 || endIndex <= startIndex)
        {
            return false;
        }

        var segments = value[(startIndex + 1)..endIndex]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if ((isRgb && segments.Length != 3) || (isRgba && segments.Length != 4))
        {
            return false;
        }

        if (!byte.TryParse(segments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var red) ||
            !byte.TryParse(segments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var green) ||
            !byte.TryParse(segments[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var blue))
        {
            return false;
        }

        byte alpha = 255;
        if (isRgba)
        {
            if (!double.TryParse(segments[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var alphaValue))
            {
                return false;
            }

            alpha = alphaValue <= 1d
                ? (byte)Math.Clamp(Math.Round(alphaValue * 255d), 0d, 255d)
                : (byte)Math.Clamp(Math.Round(alphaValue), 0d, 255d);
        }

        color = Color.FromArgb(alpha, red, green, blue);
        return true;
    }

    private static bool TryParseNamedColor(string value, out Color color)
    {
        color = value.ToLowerInvariant() switch
        {
            "black" => Colors.Black,
            "white" => Colors.White,
            "gray" or "grey" => Colors.Gray,
            "red" => Colors.Red,
            "orange" => Colors.Orange,
            "yellow" => Colors.Yellow,
            "green" => Colors.Green,
            "blue" => Colors.Blue,
            "purple" => Colors.Purple,
            "pink" => Colors.Pink,
            _ => Colors.Transparent
        };

        return !color.Equals(Colors.Transparent) || string.Equals(value, "transparent", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record class EditorCapabilities
{
    public IReadOnlyList<EditorFontFamilyOption> Fonts { get; init; } = Array.Empty<EditorFontFamilyOption>();
    public IReadOnlyList<int> FontSizes { get; init; } = Array.Empty<int>();
    public IReadOnlyList<EditorColorOption> TextColors { get; init; } = Array.Empty<EditorColorOption>();
    public IReadOnlyList<EditorColorOption> HighlightColors { get; init; } = Array.Empty<EditorColorOption>();
    public IReadOnlyList<EditorParagraphStyleOption> ParagraphStyles { get; init; } = Array.Empty<EditorParagraphStyleOption>();
    public IReadOnlyList<string> LineHeights { get; init; } = Array.Empty<string>();
    public IReadOnlyList<EditorTextAlignment> Alignments { get; init; } = Array.Empty<EditorTextAlignment>();
}
