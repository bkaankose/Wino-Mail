using System.Text;
using System.Text.Json;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;

namespace Wino.Editor.WebKitGtk;

/// <summary>
/// The mail reader in a WebKitGTK 6.0 web view: the Linux twin of <c>WebKitHtmlMailReaderSession</c>. Create it on the
/// GTK main thread and place <see cref="View"/> in the window; every render loads a fresh restrictive document.
/// </summary>
public sealed class WebKitGtkHtmlMailReaderSession : IHtmlMailReaderSession
{
    private readonly WebKitGtkBrowserSession _host;
    private bool _dark;
    private string? _font;
    private int _fontSize = 15;
    private ReaderAccessibilityContext? _accessibility;
    private string _originalHtml = string.Empty;

    public WebKitGtkHtmlMailReaderSession(IDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _host = new(dispatcher);
        _host.NavigationRequested += (_, e) => NavigationRequested?.Invoke(this, e);
        _host.LinkHovered += (_, e) => LinkHovered?.Invoke(this, e);
        _host.OperationFailed += (_, e) => OperationFailed?.Invoke(this, e);
        _host.ContentProcessTerminated += (_, e) => ContentProcessTerminated?.Invoke(this, e);
    }

    /// <summary>The widget to show. The session owns its configuration; the head only places it.</summary>
    public WebKit.WebView View => _host.Browser;

    public event EventHandler<RendererNavigationRequestedEventArgs>? NavigationRequested;
    public event EventHandler<Exception>? OperationFailed;
    public event EventHandler<RendererLinkHoverEventArgs>? LinkHovered;

    /// <summary>WebKit's web process ended; the document is gone until the next render.</summary>
    public event EventHandler? ContentProcessTerminated;

    /// <summary>True while the current document is loaded and its bridge answered.</summary>
    public bool IsReady => _host.IsReady;

    public void Configure(IExternalLauncher launcher) => _host.Configure(launcher);

    public Func<Uri, string?, Task<PlatformOperationResult>>? LinkOpener
    {
        get => _host.LinkOpener;
        set => _host.LinkOpener = value;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _host.Queue.RunAsync(async token =>
        {
            if (_host.IsReady) return;
            await _host.LoadAsync(RemoteContentPolicy.Blocked, _dark, token);
            await ApplyPresentationAsync();
        }, cancellationToken);

    public Task RenderAsync(HtmlMailReaderRequest request, CancellationToken cancellationToken = default) =>
        _host.Queue.RunAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(request);
            // Every presentation (including remote toggles) receives a fresh restrictive policy and generation.
            await _host.LoadAsync(request.ResourcePolicy, _dark, token);
            await ApplyPresentationAsync();
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Html));
            await _host.EvaluateAsync($"window.WinoRenderer.render({Quote(encoded)}, {Bool(request.ShouldLinkify)}, {(int)request.RenderMode})");
            _originalHtml = request.Html;
        }, cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        _host.Queue.RunAsync(async token =>
        {
            await _host.LoadAsync(RemoteContentPolicy.Blocked, _dark, token);
            await ApplyPresentationAsync();
            _originalHtml = string.Empty;
        }, cancellationToken);

    public Task<string> GetOriginalHtmlAsync(CancellationToken cancellationToken = default) =>
        _host.Queue.RunAsync(() => Task.FromResult(_originalHtml), cancellationToken);

    public Task SetThemeAsync(bool isDarkMode, CancellationToken cancellationToken = default) =>
        _host.Queue.RunAsync(async () =>
        {
            _dark = isDarkMode;
            if (_host.IsReady) await _host.EvaluateAsync($"window.WinoRenderer.setTheme({Bool(_dark)})");
        }, cancellationToken);

    public Task SetReaderTypographyAsync(string? fontFamily, int fontSize, CancellationToken cancellationToken = default) =>
        _host.Queue.RunAsync(async () =>
        {
            _font = fontFamily;
            _fontSize = Math.Clamp(fontSize, 8, 72);
            if (_host.IsReady) await ApplyPresentationAsync();
        }, cancellationToken);

    public Task SetAccessibilityContextAsync(ReaderAccessibilityContext context, CancellationToken cancellationToken = default) =>
        _host.Queue.RunAsync(async () =>
        {
            _accessibility = context;
            if (_host.IsReady) await ApplyPresentationAsync();
        }, cancellationToken);

    public Task EnterIdleAsync(CancellationToken cancellationToken = default) => ClearAsync(cancellationToken);

    private async Task ApplyPresentationAsync()
    {
        await _host.EvaluateAsync($"window.WinoRenderer.setTypography({Quote(_font)}, {_fontSize})");
        if (_accessibility is { } a)
            await _host.EvaluateAsync($"window.WinoRenderer.setAccessibility({Quote(a.Subject)}, {Quote(a.Sender)}, {Quote(a.Date)}, {Quote(a.BodyAutomationName)}, {Quote(a.PlainTextFallbackAutomationName)}, {Quote(a.AccessibleText)})");
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync(() =>
    {
        NavigationRequested = null;
        OperationFailed = null;
        LinkHovered = null;
        ContentProcessTerminated = null;
        _originalHtml = string.Empty;
    });

    private static string Quote(string? value) => JsonSerializer.Serialize(value, EditorJsonContext.Default.String);
    private static string Bool(bool value) => value ? "true" : "false";
}
