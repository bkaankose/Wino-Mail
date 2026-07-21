using System.Text.Json.Serialization;

namespace Wino.Mail.Editor;

public sealed record EditorMessage
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("state")]
    public EditorSelectionState? State { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public sealed record EditorSelectionState
{
    [JsonPropertyName("bold")]
    public bool Bold { get; init; }

    [JsonPropertyName("italic")]
    public bool Italic { get; init; }

    [JsonPropertyName("underline")]
    public bool Underline { get; init; }

    [JsonPropertyName("strikethrough")]
    public bool Strikethrough { get; init; }

    [JsonPropertyName("color")]
    public string Color { get; init; } = "#000000";

    [JsonPropertyName("fontFamily")]
    public string FontFamily { get; init; } = string.Empty;

    [JsonPropertyName("orderedList")]
    public bool OrderedList { get; init; }

    [JsonPropertyName("unorderedList")]
    public bool UnorderedList { get; init; }

    [JsonPropertyName("alignment")]
    public string Alignment { get; init; } = "left";

    [JsonPropertyName("inTable")]
    public bool InTable { get; init; }

    [JsonPropertyName("imageSelected")]
    public bool ImageSelected { get; init; }

    [JsonPropertyName("hasSelection")]
    public bool HasSelection { get; init; }

    [JsonPropertyName("selectedText")]
    public string SelectedText { get; init; } = string.Empty;

    [JsonPropertyName("fontSize")]
    public int? FontSize { get; init; }

    [JsonPropertyName("paragraphStyle")]
    public string ParagraphStyle { get; init; } = "p";

    [JsonPropertyName("highlightColor")]
    public string HighlightColor { get; init; } = "transparent";

    [JsonPropertyName("lineHeight")]
    public string? LineHeight { get; init; }

    [JsonPropertyName("linkUrl")]
    public string? LinkUrl { get; init; }

    [JsonPropertyName("darkMode")]
    public bool DarkMode { get; init; }

    [JsonPropertyName("spellCheck")]
    public bool SpellCheck { get; init; } = true;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EditorMessage))]
[JsonSerializable(typeof(EditorSelectionState))]
[JsonSerializable(typeof(RendererMessage))]
[JsonSerializable(typeof(string))]
internal sealed partial class EditorJsonContext : JsonSerializerContext;
