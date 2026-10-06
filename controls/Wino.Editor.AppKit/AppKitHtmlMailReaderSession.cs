using Foundation;
using System.Text;
using System.Text.Json;
using AppKit;
using WebKit;

using Wino.Core.Domain.Interfaces;
using Wino.Editor;

namespace Wino.Editor.AppKit;

public sealed class AppKitHtmlMailReaderSession : NSObject, IHtmlMailReaderSession
{
    private readonly AppKitBrowserSession _host;
    private bool _dark;
    private string? _font;
    private int _fontSize = 15;
    private ReaderAccessibilityContext? _accessibility;
    private string _originalHtml = string.Empty;
    private double _zoom = 100;
    public IHtmlMailReaderSession Session => this;
    public event EventHandler<RendererNavigationRequestedEventArgs>? NavigationRequested;
    public event EventHandler<Exception>? OperationFailed;

    public AppKitHtmlMailReaderSession(WKWebView browser)
    {
        _host = new(browser);
        _host.NavigationRequested += (_, e) => NavigationRequested?.Invoke(this, e);
        _host.OperationFailed += (_, e) => OperationFailed?.Invoke(this, e);
    }

    public void Configure(IExternalLauncher launcher) => _host.Configure(launcher);
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _host.Queue.RunAsync(EnsureReadyAsync, cancellationToken);

    private async Task EnsureReadyAsync(CancellationToken token)
    {
        if (!_host.IsReady)
        {
            await _host.LoadAsync(false, RemoteContentPolicy.Blocked, _dark, token);
            await ApplyPresentationAsync();
        }
    }

    public Task RenderAsync(HtmlMailReaderRequest request, CancellationToken cancellationToken = default) =>
        _host.Queue.RunAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(request);
            // Every presentation (including remote toggles) receives a fresh restrictive policy and generation.
            await _host.LoadAsync(false, request.ResourcePolicy, _dark, token);
            await ApplyPresentationAsync();
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Html));
            await _host.EvaluateAsync($"window.WinoRenderer.render({Quote(encoded)}, {Bool(request.ShouldLinkify)}, {(int)request.RenderMode})");
            _originalHtml = request.Html;
        }, cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        _host.Queue.RunAsync(async token =>
        {
            await _host.LoadAsync(false, RemoteContentPolicy.Blocked, _dark, token);
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

    private async Task ApplyPresentationAsync()
    {
        await _host.EvaluateAsync($"document.getElementById('wino-reader').style.zoom={Quote((_zoom / 100).ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        await _host.EvaluateAsync($"window.WinoRenderer.setTypography({Quote(_font)}, {_fontSize})");
        if (_accessibility is { } a)
            await _host.EvaluateAsync($"window.WinoRenderer.setAccessibility({Quote(a.Subject)}, {Quote(a.Sender)}, {Quote(a.Date)}, {Quote(a.BodyAutomationName)}, {Quote(a.PlainTextFallbackAutomationName)}, {Quote(a.AccessibleText)})");
    }

    public Task SetZoomAsync(double percentage, CancellationToken cancellationToken = default) =>
        _host.Queue.RunAsync(async token =>
        {
            if (!double.IsFinite(percentage)) throw new ArgumentOutOfRangeException(nameof(percentage));
            _zoom = Math.Clamp(percentage, 50, 200);
            await EnsureReadyAsync(token);
            await ApplyPresentationAsync();
        }, cancellationToken);

    /// <summary>Finds plain text in the sanitized shadow tree without changing message HTML.</summary>
    public Task<string> FindTextAsync(string query, CancellationToken cancellationToken = default) =>
        _host.Queue.RunAsync(async token =>
        {
            await EnsureReadyAsync(token);
            return await _host.EvaluateAsync("""
                (() => {
                    const query = QUERY;
                    const root = document.getElementById('wino-reader').shadowRoot;
                    const selection = window.getSelection();
                    if (!query) { selection?.removeAllRanges(); window.__winoFind = null; return ''; }
                    const nodes = [];
                    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
                        acceptNode: node => node.parentElement?.closest('style,script') ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT
                    });
                    let node; let text = '';
                    while ((node = walker.nextNode())) { nodes.push({node, start:text.length}); text += node.textContent; }
                    const last = window.__winoFind;
                    const expression = new RegExp(query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'giu');
                    expression.lastIndex = last?.query === query ? last.end : 0;
                    let match = expression.exec(text);
                    if (!match) { expression.lastIndex = 0; match = expression.exec(text); }
                    if (!match) { window.__winoFind = null; selection?.removeAllRanges(); return '0'; }
                    const index = match.index;
                    const end = index + match[0].length;
                    const startNode = nodes.find(item => item.start + item.node.length > index);
                    const endNode = nodes.find(item => item.start + item.node.length >= end);
                    if (!startNode || !endNode) return '0';
                    const range = document.createRange();
                    range.setStart(startNode.node, index - startNode.start); range.setEnd(endNode.node, end - endNode.start);
                    selection?.removeAllRanges(); selection?.addRange(range);
                    startNode.node.parentElement?.scrollIntoView({block:'center'});
                    window.__winoFind = {query, end};
                    return '1';
                })()
                """.Replace("QUERY", Quote(query)));
        }, cancellationToken);

    public Task EnterIdleAsync(CancellationToken cancellationToken = default) => ClearAsync(cancellationToken);
    public ValueTask DisposeAsync() => _host.DisposeAsync(() =>
    {
        NavigationRequested = null;
        OperationFailed = null;
        _originalHtml = string.Empty;

    });
    internal static string Quote(string? value) => JsonSerializer.Serialize(value, EditorJsonContext.Default.String);
    internal static string Bool(bool value) => value ? "true" : "false";
}
