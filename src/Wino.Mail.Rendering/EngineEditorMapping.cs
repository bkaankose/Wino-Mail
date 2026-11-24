using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Wino.Editor;
using Wino.Renderer;
using Wino.Renderer.Editing;
using Wino.Renderer.Platform;
using Wino.Renderer.Primitives;

namespace Wino.Mail.Rendering;

/// <summary>
/// Translates between the editor contract the command bar speaks (<c>Wino.Editor</c>) and the engine's editing types.
/// </summary>
internal static class EngineEditorMapping
{
    private const string DefaultLineHeight = "normal";

    private static readonly string[] FallbackFonts = ["Segoe UI", "Arial", "Calibri", "Times New Roman", "Courier New"];

    private static readonly EditorParagraphStyleOption[] ParagraphStyles =
    [
        new("Normal", "p"), new("Heading 1", "h1"), new("Heading 2", "h2"), new("Heading 3", "h3"),
        new("Quote", "blockquote"), new("Preformatted", "pre"), new("Code", "code")
    ];

    public static EditorCommandCapabilities CreateCapabilities(IPlatformBackend? backend) => new()
    {
        Fonts = SystemFonts(backend),
        FontSizes = (int[])[8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 48, 72],
        TextColors = (EditorColorValue[])
        [
            new("Default", string.Empty), new("Black", "#000000"), new("Gray", "#666666"),
            new("Red", "#c62828"), new("Orange", "#ef6c00"), new("Yellow", "#f9a825"),
            new("Green", "#2e7d32"), new("Blue", "#1565c0"), new("Purple", "#6a1b9a")
        ],
        HighlightColors = (EditorColorValue[])
        [
            new("None", string.Empty), new("Yellow", "#fff59d"), new("Green", "#c8e6c9"),
            new("Blue", "#bbdefb"), new("Pink", "#f8bbd0"), new("Orange", "#ffe0b2")
        ],
        ParagraphStyles = ParagraphStyles,
        LineHeights = (string[])[DefaultLineHeight, "1", "1.15", "1.5", "2"],
        Alignments = Enum.GetValues<EditorTextAlignment>()
    };

    private static string[] SystemFonts(IPlatformBackend? backend)
    {
        try
        {
            var fonts = backend?.Text.GetSystemFontFamilies()
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            return fonts is { Length: > 0 } ? fonts : FallbackFonts;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            return FallbackFonts;
        }
    }

    /// <summary>
    /// The engine reports only formatting the text carries itself; text in the composer's default font reports the
    /// default, as the command bar shows it.
    /// </summary>
    public static EditorState ToEditorState(EditState? state, string defaultFontFamily, int defaultFontSize, bool isDarkMode, bool isSpellCheckEnabled)
    {
        state ??= new EditState();

        return new EditorState
        {
            IsBold = state.IsBold,
            IsItalic = state.IsItalic,
            IsUnderline = state.IsUnderline,
            IsStrikethrough = state.IsStrikethrough,
            IsOrderedList = state.IsOrderedList,
            IsUnorderedList = state.IsUnorderedList,
            CanIndent = state.CanIndent,
            CanOutdent = state.CanOutdent,
            HasSelection = state.HasSelection,
            IsImageSelected = state.IsImageSelected,
            IsDarkMode = isDarkMode,
            IsBuiltInToolbarVisible = false,
            IsSpellCheckEnabled = isSpellCheckEnabled,
            Alignment = ToEditorAlignment(state.Alignment),
            FontFamily = FirstFontFamily(state.FontFamily) ?? defaultFontFamily,
            FontSize = state.FontSizePx ?? defaultFontSize,
            ParagraphStyle = ToTag(state.ParagraphStyle),
            TextColor = state.TextColor,
            HighlightColor = state.HighlightColor,
            LineHeight = string.IsNullOrWhiteSpace(state.LineHeight) ? DefaultLineHeight : state.LineHeight,
            LinkUrl = state.LinkUrl,
            ImageAltText = state.ImageAltText,
            ImageLinkUrl = state.ImageLinkUrl,
            SelectedText = state.SelectedText
        };
    }

    public static TextAlignment ToEngineAlignment(EditorTextAlignment alignment) => alignment switch
    {
        EditorTextAlignment.Center => TextAlignment.Center,
        EditorTextAlignment.Right => TextAlignment.Right,
        EditorTextAlignment.Justify => TextAlignment.Justify,
        _ => TextAlignment.Left
    };

    public static EditorTextAlignment ToEditorAlignment(TextAlignment alignment) => alignment switch
    {
        TextAlignment.Center => EditorTextAlignment.Center,
        TextAlignment.Right => EditorTextAlignment.Right,
        TextAlignment.Justify => EditorTextAlignment.Justify,
        _ => EditorTextAlignment.Left
    };

    public static ParagraphStyle ToParagraphStyle(string? tag) => tag?.Trim().ToLowerInvariant() switch
    {
        "h1" => ParagraphStyle.Heading1,
        "h2" => ParagraphStyle.Heading2,
        "h3" => ParagraphStyle.Heading3,
        "blockquote" => ParagraphStyle.Quote,
        "pre" => ParagraphStyle.Preformatted,
        "code" => ParagraphStyle.Code,
        _ => ParagraphStyle.Paragraph
    };

    public static string ToTag(ParagraphStyle style) => style switch
    {
        ParagraphStyle.Heading1 => "h1",
        ParagraphStyle.Heading2 => "h2",
        ParagraphStyle.Heading3 => "h3",
        ParagraphStyle.Quote => "blockquote",
        ParagraphStyle.Preformatted => "pre",
        ParagraphStyle.Code => "code",
        _ => "p"
    };

    /// <summary>Null for the "Default" and "None" entries, which clear the color.</summary>
    public static Rgba? ToColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var color = EditorColors.Parse(value);
        return color.A == 0 ? null : color;
    }

    private static string? FirstFontFamily(string? fontFamily)
    {
        if (string.IsNullOrWhiteSpace(fontFamily)) return null;

        var first = fontFamily.Split(',', 2, StringSplitOptions.TrimEntries)[0].Trim('\'', '"', ' ');
        return first.Length == 0 ? null : first;
    }

    /// <summary>
    /// The engine key for a shortcut key as the keyboard shortcut service stores it: a Windows virtual-key name
    /// (<c>A</c>, <c>Number5</c>, <c>F2</c>, <c>PageUp</c>) or its numeric code; <see cref="Key.Unknown"/> when the
    /// engine has no such key. The names are the stored format on every platform, so the table is spelled out here.
    /// </summary>
    public static Key ToKey(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return Key.Unknown;

        var name = keyName.Trim();
        if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            return FromVirtualKeyCode(code);

        if (name.Length == 1 && char.IsAsciiLetter(name[0]))
            return (Key)((int)Key.A + (char.ToUpperInvariant(name[0]) - 'A'));

        if (TryDigit(name, "NumberPad", out var pad)) return (Key)((int)Key.D0 + pad);
        if (TryDigit(name, "Number", out var digit)) return (Key)((int)Key.D0 + digit);

        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var function)
            && function is >= 1 and <= 12)
            return (Key)((int)Key.F1 + function - 1);

        return name.ToLowerInvariant() switch
        {
            "enter" => Key.Enter,
            "tab" => Key.Tab,
            "back" => Key.Backspace,
            "delete" => Key.Delete,
            "escape" => Key.Escape,
            "space" => Key.Space,
            "left" => Key.Left,
            "right" => Key.Right,
            "up" => Key.Up,
            "down" => Key.Down,
            "home" => Key.Home,
            "end" => Key.End,
            "pageup" => Key.PageUp,
            "pagedown" => Key.PageDown,
            "insert" => Key.Insert,
            _ => Key.Unknown
        };
    }

    private static bool TryDigit(string name, string prefix, out int digit)
    {
        digit = 0;
        return name.Length == prefix.Length + 1 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(name[^1])
            && (digit = name[^1] - '0') >= 0;
    }

    // Windows virtual-key codes, for a shortcut stored as a number.
    private static Key FromVirtualKeyCode(int code) => code switch
    {
        >= 65 and <= 90 => (Key)((int)Key.A + code - 65),
        >= 48 and <= 57 => (Key)((int)Key.D0 + code - 48),
        >= 96 and <= 105 => (Key)((int)Key.D0 + code - 96),
        >= 112 and <= 123 => (Key)((int)Key.F1 + code - 112),
        13 => Key.Enter,
        9 => Key.Tab,
        8 => Key.Backspace,
        46 => Key.Delete,
        27 => Key.Escape,
        32 => Key.Space,
        37 => Key.Left,
        39 => Key.Right,
        38 => Key.Up,
        40 => Key.Down,
        36 => Key.Home,
        35 => Key.End,
        33 => Key.PageUp,
        34 => Key.PageDown,
        45 => Key.Insert,
        _ => Key.Unknown
    };

    /// <summary>
    /// Keys the editor keeps for itself, so a host gesture on one of them never replaces typing or a standard command.
    /// </summary>
    public static bool IsEditingKey(Key key) => key is Key.A or Key.B or Key.C or Key.F or Key.I or Key.K or Key.N or Key.O or Key.P
        or Key.R or Key.S or Key.U or Key.V or Key.W or Key.X or Key.Y or Key.Z or Key.Tab or Key.Delete or Key.Backspace;
}
