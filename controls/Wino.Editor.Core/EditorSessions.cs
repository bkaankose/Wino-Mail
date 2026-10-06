namespace Wino.Editor;

public enum HtmlMailRenderMode { Original = 0, Readability = 1 }
public enum RemoteContentPolicy { Blocked, ImagesAndFontsAllowed }
public sealed record HtmlMailReaderRequest(string Html, RemoteContentPolicy ResourcePolicy,
    HtmlMailRenderMode RenderMode = HtmlMailRenderMode.Original, bool ShouldLinkify = true);
public sealed record ReaderAccessibilityContext(string? Subject, string? Sender, string? Date,
    string? BodyAutomationName, string? PlainTextFallbackAutomationName, string? AccessibleText);
public sealed record EditorColorValue(string Name, string Value);
public sealed record EditorCommandCapabilities
{
    public IReadOnlyList<string> Fonts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<int> FontSizes { get; init; } = Array.Empty<int>();
    public IReadOnlyList<EditorColorValue> TextColors { get; init; } = Array.Empty<EditorColorValue>();
    public IReadOnlyList<EditorColorValue> HighlightColors { get; init; } = Array.Empty<EditorColorValue>();
    public IReadOnlyList<EditorParagraphStyleOption> ParagraphStyles { get; init; } = Array.Empty<EditorParagraphStyleOption>();
    public IReadOnlyList<string> LineHeights { get; init; } = Array.Empty<string>();
    public IReadOnlyList<EditorTextAlignment> Alignments { get; init; } = Array.Empty<EditorTextAlignment>();
}

public interface IHtmlMailEditorSession : IEditorCommandTarget, IAsyncDisposable
{
    event EventHandler? ContentChanged;
    event EventHandler<EditorApplicationShortcutGesture>? ApplicationShortcutRequested;
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task RenderHtmlAsync(string htmlBody, CancellationToken cancellationToken = default);
    Task<string?> GetHtmlBodyAsync(CancellationToken cancellationToken = default);
    Task SetThemeAsync(bool isDarkMode, CancellationToken cancellationToken = default);
    Task SetDefaultTypographyAsync(string? fontFamily, int fontSize, CancellationToken cancellationToken = default);
    Task FocusEditorAsync(bool focusControlAsWell, CancellationToken cancellationToken = default);
    Task SetApplicationShortcutsAsync(IReadOnlyList<EditorApplicationShortcutGesture> shortcuts, CancellationToken cancellationToken = default);
    Task InsertImagesAsync(IEnumerable<EditorImageInfo> images, CancellationToken cancellationToken = default);
    Task ExecuteCommandAsync(EditorCommand command, CancellationToken cancellationToken);
}

public interface IHtmlMailReaderSession : IAsyncDisposable
{
    event EventHandler<RendererNavigationRequestedEventArgs>? NavigationRequested;
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task RenderAsync(HtmlMailReaderRequest request, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
    Task<string> GetOriginalHtmlAsync(CancellationToken cancellationToken = default);
    Task SetThemeAsync(bool isDarkMode, CancellationToken cancellationToken = default);
    Task SetReaderTypographyAsync(string? fontFamily, int fontSize, CancellationToken cancellationToken = default);
    Task SetAccessibilityContextAsync(ReaderAccessibilityContext context, CancellationToken cancellationToken = default);
    Task EnterIdleAsync(CancellationToken cancellationToken = default);
}
