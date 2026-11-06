namespace Wino.Editor;
public sealed record RendererNavigationRequestedEventArgs(Uri Uri);

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

    [System.Text.Json.Serialization.JsonPropertyName("requestId")]
    public int? RequestId { get; init; }
}
