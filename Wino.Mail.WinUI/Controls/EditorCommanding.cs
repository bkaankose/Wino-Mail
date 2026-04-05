using System;
using System.Globalization;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Wino.Mail.Controls;

public interface IEditorCommandTarget
{
    EditorState CurrentState { get; }
    EditorCapabilities Capabilities { get; }
    event EventHandler<EditorState>? StateChanged;
    event EventHandler<EditorCapabilities>? CapabilitiesChanged;
    Task ExecuteCommandAsync(EditorCommand command);
}

public interface IEditorCommandControl
{
    IEditorCommandTarget? CommandTarget { get; set; }
    void AttachCommandTarget(IEditorCommandTarget? target);
    void DetachCommandTarget();
}

public enum EditorCommandKind
{
    ToggleBold,
    ToggleItalic,
    ToggleUnderline,
    ToggleStrikethrough,
    ToggleOrderedList,
    ToggleUnorderedList,
    Indent,
    Outdent,
    SetAlignment,
    SetFontFamily,
    SetFontSize,
    SetParagraphStyle,
    SetTextColor,
    SetHighlightColor,
    SetLineHeight,
    InsertImage,
    InsertLink,
    RemoveLink,
    InsertEmoji,
    InsertTable,
    ToggleBuiltInToolbar,
    ToggleTheme,
    ToggleSpellCheck
}

public enum EditorTextAlignment
{
    Left,
    Center,
    Right,
    Justify
}

public sealed record class EditorCommand(EditorCommandKind Kind, object? Value = null)
{
    public static EditorCommand ToggleBold() => new(EditorCommandKind.ToggleBold);
    public static EditorCommand ToggleItalic() => new(EditorCommandKind.ToggleItalic);
    public static EditorCommand ToggleUnderline() => new(EditorCommandKind.ToggleUnderline);
    public static EditorCommand ToggleStrikethrough() => new(EditorCommandKind.ToggleStrikethrough);
    public static EditorCommand ToggleOrderedList() => new(EditorCommandKind.ToggleOrderedList);
    public static EditorCommand ToggleUnorderedList() => new(EditorCommandKind.ToggleUnorderedList);
    public static EditorCommand Indent() => new(EditorCommandKind.Indent);
    public static EditorCommand Outdent() => new(EditorCommandKind.Outdent);
    public static EditorCommand SetAlignment(EditorTextAlignment alignment) => new(EditorCommandKind.SetAlignment, alignment);
    public static EditorCommand SetFontFamily(string fontFamily) => new(EditorCommandKind.SetFontFamily, fontFamily);
    public static EditorCommand SetFontSize(int fontSize) => new(EditorCommandKind.SetFontSize, fontSize);
    public static EditorCommand SetParagraphStyle(string tagName) => new(EditorCommandKind.SetParagraphStyle, tagName);
    public static EditorCommand SetTextColor(string color) => new(EditorCommandKind.SetTextColor, color);
    public static EditorCommand SetHighlightColor(string color) => new(EditorCommandKind.SetHighlightColor, color);
    public static EditorCommand SetLineHeight(string lineHeight) => new(EditorCommandKind.SetLineHeight, lineHeight);
    public static EditorCommand InsertImage() => new(EditorCommandKind.InsertImage);
    public static EditorCommand InsertEmoji() => new(EditorCommandKind.InsertEmoji);
    public static EditorCommand InsertLink(EditorLinkCommandArgs args) => new(EditorCommandKind.InsertLink, args);
    public static EditorCommand RemoveLink() => new(EditorCommandKind.RemoveLink);
    public static EditorCommand InsertTable(EditorTableCommandArgs args) => new(EditorCommandKind.InsertTable, args);
    public static EditorCommand ToggleBuiltInToolbar(bool isVisible) => new(EditorCommandKind.ToggleBuiltInToolbar, isVisible);
    public static EditorCommand ToggleTheme(bool isDarkMode) => new(EditorCommandKind.ToggleTheme, isDarkMode);
    public static EditorCommand ToggleSpellCheck(bool isEnabled) => new(EditorCommandKind.ToggleSpellCheck, isEnabled);
}

public sealed record class EditorLinkCommandArgs(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("openInNewWindow")] bool OpenInNewWindow = true);

public sealed record class EditorTableCommandArgs(
    [property: JsonPropertyName("rows")] int Rows,
    [property: JsonPropertyName("columns")] int Columns);

public sealed record class EditorColorOption(string Name, string Value)
{
    public SolidColorBrush Brush => new(ParseColorValue(Value));

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

public sealed record class EditorParagraphStyleOption(string Name, string Tag);

public sealed record class EditorCapabilities
{
    public IReadOnlyList<string> Fonts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<int> FontSizes { get; init; } = Array.Empty<int>();
    public IReadOnlyList<EditorColorOption> TextColors { get; init; } = Array.Empty<EditorColorOption>();
    public IReadOnlyList<EditorColorOption> HighlightColors { get; init; } = Array.Empty<EditorColorOption>();
    public IReadOnlyList<EditorParagraphStyleOption> ParagraphStyles { get; init; } = Array.Empty<EditorParagraphStyleOption>();
    public IReadOnlyList<string> LineHeights { get; init; } = Array.Empty<string>();
    public IReadOnlyList<EditorTextAlignment> Alignments { get; init; } = Array.Empty<EditorTextAlignment>();
}

public sealed record class EditorState
{
    public bool IsBold { get; init; }
    public bool IsItalic { get; init; }
    public bool IsUnderline { get; init; }
    public bool IsStrikethrough { get; init; }
    public bool IsOrderedList { get; init; }
    public bool IsUnorderedList { get; init; }
    public bool CanIndent { get; init; } = true;
    public bool CanOutdent { get; init; }
    public bool HasSelection { get; init; }
    public bool IsDarkMode { get; init; }
    public bool IsBuiltInToolbarVisible { get; init; }
    public bool IsSpellCheckEnabled { get; init; } = true;
    public EditorTextAlignment Alignment { get; init; } = EditorTextAlignment.Left;
    public string? FontFamily { get; init; }
    public int? FontSize { get; init; }
    public string? ParagraphStyle { get; init; }
    public string? TextColor { get; init; }
    public string? HighlightColor { get; init; }
    public string? LineHeight { get; init; }
    public string? LinkUrl { get; init; }
    public string? SelectedText { get; init; }
}
