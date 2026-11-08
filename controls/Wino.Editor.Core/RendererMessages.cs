namespace Wino.Editor;
/// <param name="LinkText">The anchor's visible text when the reader captured the click, or null.</param>
public sealed record RendererNavigationRequestedEventArgs(Uri Uri, string? LinkText = null);

/// <summary>
/// The reader reported the link under the pointer or keyboard focus. <see cref="Uri"/> is null when no link is hovered
/// any more; <see cref="SafeLinkTarget"/> is the address an Outlook Safe Links redirect leads to, or null.
/// </summary>
public sealed record RendererLinkHoverEventArgs(string? Uri, string? SafeLinkTarget);

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
