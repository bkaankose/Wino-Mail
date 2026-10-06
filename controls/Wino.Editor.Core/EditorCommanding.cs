using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Wino.Editor;

public interface IEditorCommandTarget
{
    EditorState CurrentState { get; }
    EditorCommandCapabilities Capabilities { get; }
    event EventHandler<EditorState>? StateChanged;
    event EventHandler<EditorShortcutKind>? ShortcutRequested;
    Task ExecuteCommandAsync(EditorCommand command);
}

public sealed record class EditorApplicationShortcutGesture(
    string Key,
    bool Control,
    bool Alt,
    bool Shift,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Command = null);

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
    ClearFormatting,
    InsertImage,
    SetImageProperties,
    InsertLink,
    RemoveLink,
    InsertEmoji,
    InsertTable,
    ToggleBuiltInToolbar,
    ToggleTheme,
    ToggleSpellCheck,
    SetSpellCheckLanguage,
    ToggleAutoCorrect,
    Undo,
    Redo
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
    public static EditorCommand ClearFormatting() => new(EditorCommandKind.ClearFormatting);
    public static EditorCommand InsertImage() => new(EditorCommandKind.InsertImage);
    public static EditorCommand SetImageProperties(EditorImagePropertiesCommandArgs args) => new(EditorCommandKind.SetImageProperties, args);
    public static EditorCommand InsertEmoji() => new(EditorCommandKind.InsertEmoji);
    public static EditorCommand InsertLink(EditorLinkCommandArgs args) => new(EditorCommandKind.InsertLink, args);
    public static EditorCommand RemoveLink() => new(EditorCommandKind.RemoveLink);
    public static EditorCommand InsertTable(EditorTableCommandArgs args) => new(EditorCommandKind.InsertTable, args);
    public static EditorCommand ToggleBuiltInToolbar(bool isVisible) => new(EditorCommandKind.ToggleBuiltInToolbar, isVisible);
    public static EditorCommand ToggleTheme(bool isDarkMode) => new(EditorCommandKind.ToggleTheme, isDarkMode);
    public static EditorCommand ToggleSpellCheck(bool isEnabled) => new(EditorCommandKind.ToggleSpellCheck, isEnabled);
    public static EditorCommand SetSpellCheckLanguage(string languageCode) => new(EditorCommandKind.SetSpellCheckLanguage, languageCode);
    public static EditorCommand Undo() => new(EditorCommandKind.Undo);
    public static EditorCommand Redo() => new(EditorCommandKind.Redo);
    public static EditorCommand ToggleAutoCorrect(bool isEnabled) => new(EditorCommandKind.ToggleAutoCorrect, isEnabled);
}

public sealed record class EditorLinkCommandArgs(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("openInNewWindow")] bool OpenInNewWindow = true);

public sealed record class EditorTableCommandArgs(
    [property: JsonPropertyName("rows")] int Rows,
    [property: JsonPropertyName("columns")] int Columns);

public sealed record class EditorImagePropertiesCommandArgs(
    [property: JsonPropertyName("altText")] string AltText,
    [property: JsonPropertyName("linkUrl")] string? LinkUrl = null,
    [property: JsonPropertyName("openInNewWindow")] bool OpenInNewWindow = true);

public sealed record class EditorImageInfo(string Data, string Name);

public enum EditorShortcutKind
{
    OpenLinkDialog
}

public sealed record class EditorParagraphStyleOption(string Name, string Tag);

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
    public bool IsImageSelected { get; init; }
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
    public string? ImageAltText { get; init; }
    public string? ImageLinkUrl { get; init; }
    public string? SelectedText { get; init; }
}
