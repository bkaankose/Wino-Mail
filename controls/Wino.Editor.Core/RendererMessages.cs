namespace Wino.Editor;

public sealed record RendererMessage
{
    [System.Text.Json.Serialization.JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string? Type { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>For linkHover: the address an Outlook Safe Links redirect leads to, or empty.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("target")]
    public string? Target { get; init; }

    /// <summary>For navigation: the clicked anchor's visible text, or empty.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("text")]
    public string? Text { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("requestId")]
    public int? RequestId { get; init; }
}
