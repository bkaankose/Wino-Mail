namespace Wino.Editor;
/// <param name="LinkText">The anchor's visible text when the reader captured the click, or null.</param>
public sealed record RendererNavigationRequestedEventArgs(Uri Uri, string? LinkText = null);

/// <summary>
/// The reader reported the link under the pointer or keyboard focus. <see cref="Uri"/> is null when no link is hovered
/// any more; <see cref="SafeLinkTarget"/> is the address an Outlook Safe Links redirect leads to, or null.
/// </summary>
public sealed record RendererLinkHoverEventArgs(string? Uri, string? SafeLinkTarget);
