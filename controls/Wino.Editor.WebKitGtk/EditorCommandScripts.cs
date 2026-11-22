using System.Text;
using System.Text.Json;

namespace Wino.Editor.WebKitGtk;

internal static class EditorCommandScripts
{
    public static string? ExecName(EditorCommand command) => command.Kind switch
    {
        EditorCommandKind.ToggleBold => "bold",
        EditorCommandKind.ToggleItalic => "italic",
        EditorCommandKind.ToggleUnderline => "underline",
        EditorCommandKind.ToggleStrikethrough => "strikeThrough",
        EditorCommandKind.ToggleOrderedList => "insertOrderedList",
        EditorCommandKind.ToggleUnorderedList => "insertUnorderedList",
        EditorCommandKind.Indent => "indent",
        EditorCommandKind.Outdent => "outdent",
        EditorCommandKind.Undo => "undo",
        EditorCommandKind.Redo => "redo",
        EditorCommandKind.ClearFormatting => "removeFormat",
        EditorCommandKind.SetFontFamily => "fontName",
        EditorCommandKind.SetFontSize => "fontSize",
        EditorCommandKind.SetTextColor => "foreColor",
        EditorCommandKind.SetHighlightColor => "backColor",
        EditorCommandKind.SetAlignment => command.Value is EditorTextAlignment alignment
            ? alignment switch
            {
                EditorTextAlignment.Center => "justifyCenter",
                EditorTextAlignment.Right => "justifyRight",
                EditorTextAlignment.Justify => "justifyFull",
                _ => "justifyLeft"
            }
            : throw new ArgumentException("Alignment is required."),
        _ => null
    };

    public static string Expression(EditorCommand command, EditorState state) => command.Kind switch
    {
        EditorCommandKind.SetParagraphStyle => $"window.WinoEditor.setParagraphStyle({Quote(command.Value?.ToString() ?? "p")})",
        EditorCommandKind.SetLineHeight => $"window.WinoEditor.setLineHeight({Quote(command.Value?.ToString() ?? "normal")})",
        EditorCommandKind.SetImageProperties when command.Value is EditorImagePropertiesCommandArgs image
            => $"window.WinoEditorImages.setSelectedProperties({JsonSerializer.Serialize(image, EditorJsonContext.Default.EditorImagePropertiesCommandArgs)})",
        EditorCommandKind.InsertLink when command.Value is EditorLinkCommandArgs link => state.IsImageSelected
            ? $"window.WinoEditorImages.setSelectedProperties({JsonSerializer.Serialize(new EditorImagePropertiesCommandArgs(state.ImageAltText ?? string.Empty, link.Url, link.OpenInNewWindow), EditorJsonContext.Default.EditorImagePropertiesCommandArgs)})"
            : $"window.WinoEditor.createLink({Quote(link.Url)}, {Quote(link.Text)}, {Bool(link.OpenInNewWindow)})",
        EditorCommandKind.RemoveLink => state.IsImageSelected
            ? $"window.WinoEditorImages.setSelectedProperties({JsonSerializer.Serialize(new EditorImagePropertiesCommandArgs(state.ImageAltText ?? string.Empty), EditorJsonContext.Default.EditorImagePropertiesCommandArgs)})"
            : "window.WinoEditor.removeLink()",
        EditorCommandKind.InsertEmoji => $"window.WinoEditor.insertEmoji({Quote(command.Value?.ToString() ?? "😊")})",
        EditorCommandKind.InsertTable when command.Value is EditorTableCommandArgs table
            => $"window.WinoEditor.insertTable({Math.Clamp(table.Rows, 1, 20)}, {Math.Clamp(table.Columns, 1, 20)})",
        EditorCommandKind.ToggleTheme => $"window.WinoEditor.setTheme({Bool(command.Value is true)})",
        EditorCommandKind.ToggleSpellCheck => $"window.WinoEditor.setSpellCheck({Bool(command.Value is true)})",
        EditorCommandKind.SetSpellCheckLanguage => $"window.WinoEditor.setSpellCheckLanguage({Quote(command.Value?.ToString())})",
        EditorCommandKind.ToggleAutoCorrect => $"window.WinoEditor.setAutoCorrect({Bool(command.Value is true)})",
        _ => throw new NotSupportedException($"The Linux editor cannot execute {command.Kind}.")
    };

    public static EditorState ToState(EditorSelectionState s) => new()
    {
        IsBold = s.Bold,
        IsItalic = s.Italic,
        IsUnderline = s.Underline,
        IsStrikethrough = s.Strikethrough,
        IsOrderedList = s.OrderedList,
        IsUnorderedList = s.UnorderedList,
        HasSelection = s.HasSelection,
        IsImageSelected = s.ImageSelected,
        IsDarkMode = s.DarkMode,
        IsSpellCheckEnabled = s.SpellCheck,
        Alignment = Enum.TryParse<EditorTextAlignment>(s.Alignment, true, out var alignment) ? alignment : EditorTextAlignment.Left,
        FontFamily = s.FontFamily,
        FontSize = s.FontSize,
        ParagraphStyle = s.ParagraphStyle,
        TextColor = s.Color,
        HighlightColor = s.HighlightColor,
        LineHeight = s.LineHeight,
        LinkUrl = s.LinkUrl,
        ImageAltText = s.ImageAltText,
        ImageLinkUrl = s.ImageLinkUrl,
        SelectedText = s.SelectedText
    };

    public static string Quote(string? value) => JsonSerializer.Serialize(value, EditorJsonContext.Default.String);

    public static string Bool(bool value) => value ? "true" : "false";

    public static string Base64(string? value) => string.IsNullOrEmpty(value) ? string.Empty : Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}
