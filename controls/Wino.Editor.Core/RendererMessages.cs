namespace Wino.Editor;
public sealed record RendererNavigationRequestedEventArgs(Uri Uri);

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
}
