namespace MailCal.Editor;
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

    /// <summary>For linkHover: the address an Outlook Safe Links redirect leads to, or empty.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("target")]
    public string? Target { get; init; }
}
