using System;
using System.Collections.Generic;
using System.Linq;
using Windows.System;
using Wino.Editor;
using Wino.Renderer;
using Wino.Renderer.Editing;
using Wino.Renderer.Platform;
using Wino.Renderer.Primitives;

namespace Wino.Mail.WinUI.Rendering;

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
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
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

        var color = Controls.EditorColorOption.ParseColorValue(value);
        return color.A == 0 ? null : new Rgba(color.R, color.G, color.B, color.A);
    }

    private static string? FirstFontFamily(string? fontFamily)
    {
        if (string.IsNullOrWhiteSpace(fontFamily)) return null;

        var first = fontFamily.Split(',', 2, StringSplitOptions.TrimEntries)[0].Trim('\'', '"', ' ');
        return first.Length == 0 ? null : first;
    }

    /// <summary>
    /// The engine key for a shortcut key name as the keyboard shortcut service stores it (a <see cref="VirtualKey"/>
    /// name); <see cref="Key.Unknown"/> when the engine has no such key.
    /// </summary>
    public static Key ToKey(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName) || !Enum.TryParse(keyName, true, out VirtualKey key)) return Key.Unknown;

        return key switch
        {
            >= VirtualKey.A and <= VirtualKey.Z => (Key)((int)Key.A + (key - VirtualKey.A)),
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => (Key)((int)Key.D0 + (key - VirtualKey.Number0)),
            >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => (Key)((int)Key.D0 + (key - VirtualKey.NumberPad0)),
            >= VirtualKey.F1 and <= VirtualKey.F12 => (Key)((int)Key.F1 + (key - VirtualKey.F1)),
            VirtualKey.Enter => Key.Enter,
            VirtualKey.Tab => Key.Tab,
            VirtualKey.Back => Key.Backspace,
            VirtualKey.Delete => Key.Delete,
            VirtualKey.Escape => Key.Escape,
            VirtualKey.Space => Key.Space,
            VirtualKey.Left => Key.Left,
            VirtualKey.Right => Key.Right,
            VirtualKey.Up => Key.Up,
            VirtualKey.Down => Key.Down,
            VirtualKey.Home => Key.Home,
            VirtualKey.End => Key.End,
            VirtualKey.PageUp => Key.PageUp,
            VirtualKey.PageDown => Key.PageDown,
            VirtualKey.Insert => Key.Insert,
            _ => Key.Unknown
        };
    }

    /// <summary>
    /// Keys the editor keeps for itself, so a host gesture on one of them never replaces typing or a standard command.
    /// </summary>
    public static bool IsEditingKey(Key key) => key is Key.A or Key.B or Key.C or Key.F or Key.I or Key.K or Key.N or Key.O or Key.P
        or Key.R or Key.S or Key.U or Key.V or Key.W or Key.X or Key.Y or Key.Z or Key.Tab or Key.Delete or Key.Backspace;
}
