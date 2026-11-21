using System.Text.Json;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;

namespace Wino.Editor.WebKitGtk;

// Owns the web view, its script bridge and one current document generation. Native state is touched on the GTK main thread only.
internal sealed class WebKitGtkBrowserSession
{
    private const string BridgeName = "winoMail";
    private readonly IDispatcher _dispatcher;
    private readonly WebKit.UserContentManager _content;
    private readonly GObject.SignalHandler<WebKit.UserContentManager, WebKit.UserContentManager.ScriptMessageReceivedSignalArgs> _messageHandler;
    private readonly CancellationTokenSource _linkLifetime = new();
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _sessionId;
    private bool _allowShell;
    private bool _disposed;
    private IExternalLauncher? _launcher;

    // Gir.Core resolves a binding's native library only after its module is initialized; GTK and libadwaita are
    // initialized by the application object, WebKit by nothing else.
    static WebKitGtkBrowserSession() => WebKit.Module.Initialize();

    public WebKitGtkBrowserSession(IDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _content = WebKit.UserContentManager.New();
        _content.RegisterScriptMessageHandler(BridgeName, null);
        _messageHandler = OnScriptMessage;
        WebKit.UserContentManager.ScriptMessageReceivedSignal.Connect(_content, _messageHandler, false, BridgeName);

        // An ephemeral session keeps cookies, cache and storage in memory only, like the non-persistent store on Apple.
        using var session = new GObject.Value(WebKit.NetworkSession.NewEphemeral());
        using var content = new GObject.Value(_content);
        Browser = WebKit.WebView.NewWithProperties([new GObject.ConstructArgument("network-session", session), new GObject.ConstructArgument("user-content-manager", content)]);
        var settings = Browser.GetSettings();
        settings.JavascriptCanOpenWindowsAutomatically = false;
        Browser.OnDecidePolicy += OnDecidePolicy;
        Browser.OnWebProcessTerminated += OnWebProcessTerminated;
    }

    public WebKit.WebView Browser { get; }
    public SessionOperationQueue Queue { get; } = new();
    public bool IsReady => !_disposed && _sessionId is not null && _ready.Task.IsCompletedSuccessfully;
    public event EventHandler<RendererNavigationRequestedEventArgs>? NavigationRequested;
    public event EventHandler<Exception>? OperationFailed;
    public event EventHandler<RendererLinkHoverEventArgs>? LinkHovered;

    /// <summary>WebKit's web process ended; the document is gone until the next load.</summary>
    public event EventHandler? ContentProcessTerminated;

    /// <summary>Opens clicked links in place of the launcher (the reader's link cleaning and confirmation).</summary>
    public Func<Uri, string?, Task<PlatformOperationResult>>? LinkOpener { get; set; }

    public void Configure(IExternalLauncher launcher)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(launcher);
        _launcher = launcher;
    }

    public async Task LoadAsync(RemoteContentPolicy policy, bool dark, CancellationToken token)
    {
        string id = Guid.NewGuid().ToString("N");
        string document = await AppleMailDocumentBuilder.BuildReaderAsync(id, policy, dark, token, useCommandKey: false);
        await OnUIAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sessionId = id;
            _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _allowShell = true;
            Browser.StopLoading();
            Browser.LoadHtml(document, null);
            return Task.FromResult(true);
        });
        try { await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30), token); }
        catch
        {
            await OnUIAsync(() => { _sessionId = null; Browser.StopLoading(); return Task.FromResult(true); });
            throw;
        }
    }

    public async Task<string?> EvaluateAsync(string expression)
    {
        if (!IsReady) throw new InvalidOperationException("The mail document is not ready.");
        string? generation = _sessionId;
        return await OnUIAsync(async () =>
        {
            using var value = await Browser.EvaluateJavascriptAsync("String((" + expression + ") ?? '')");
            if (_disposed || generation != _sessionId) throw new ObjectDisposedException(nameof(WebKitGtkBrowserSession));
            return (string?)value.ToString();
        });
    }

    public Task<T> OnUIAsync<T>(Func<Task<T>> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _dispatcher.ExecuteOnUIThread(async () =>
        {
            try { completion.TrySetResult(await operation()); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        return completion.Task;
    }

    // Only the one document this session just loaded may navigate; links leave through the bridge instead.
    private bool OnDecidePolicy(WebKit.WebView sender, WebKit.WebView.DecidePolicySignalArgs args)
    {
        if (args.DecisionType == WebKit.PolicyDecisionType.Response) return false;
        bool allow = false;
        if (args.DecisionType == WebKit.PolicyDecisionType.NavigationAction && args.Decision is WebKit.NavigationPolicyDecision navigation)
        {
            allow = _allowShell && navigation.GetNavigationAction().GetRequest().GetUri() == "about:blank";
            _allowShell = false;
        }
        if (allow) args.Decision.Use();
        else args.Decision.Ignore();
        return true;
    }

    private void OnWebProcessTerminated(WebKit.WebView sender, WebKit.WebView.WebProcessTerminatedSignalArgs args)
    {
        if (_disposed) return;
        _sessionId = null;
        _ready.TrySetException(new InvalidOperationException("The mail document's web process ended."));
        _ = _ready.Task.Exception;
        ContentProcessTerminated?.Invoke(this, EventArgs.Empty);
    }

    // WebKitGTK does not report the sending frame here. The C# pipeline and DOMPurify remove frames from mail HTML, and
    // a message is accepted only with the random id of the current generation.
    private void OnScriptMessage(WebKit.UserContentManager sender, WebKit.UserContentManager.ScriptMessageReceivedSignalArgs args)
    {
        if (_disposed) return;
        try
        {
            string json = args.Value.ToString();
            var message = JsonSerializer.Deserialize(json, EditorJsonContext.Default.EditorMessage);
            if (_sessionId is null || message?.SessionId != _sessionId) return;
            if (message.Type == "ready") _ready.TrySetResult();
            else if (message.Type is "bootstrapError" or "initializationError")
                _ready.TrySetException(new InvalidOperationException("The mail document failed to initialize."));
            else if (message.Type is "openLink" or "navigation")
            {
                var reader = JsonSerializer.Deserialize(json, EditorJsonContext.Default.RendererMessage);
                if (Uri.TryCreate(message.Url ?? reader?.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
                    OpenLink(uri, NullIfBlank(reader?.Text));
            }
            else if (message.Type == "linkHover")
            {
                var hover = JsonSerializer.Deserialize(json, EditorJsonContext.Default.RendererMessage);
                LinkHovered?.Invoke(this, new(NullIfBlank(hover?.Uri), NullIfBlank(hover?.Target)));
            }
        }
        catch (JsonException) { }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private async void OpenLink(Uri uri, string? text)
    {
        NavigationRequested?.Invoke(this, new(uri, text));
        if (_linkLifetime.IsCancellationRequested) return;
        try
        {
            if (LinkOpener is { } opener) (await opener(uri, text)).ThrowIfNotSucceeded();
            else if (_launcher is not null) (await _launcher.LaunchUriAsync(uri, _linkLifetime.Token)).ThrowIfNotSucceeded();
        }
        catch (Exception error) { if (!_linkLifetime.IsCancellationRequested) OperationFailed?.Invoke(this, error); }
    }

    public ValueTask DisposeAsync(Action releaseVisual) => Queue.DisposeAsync(async () =>
    {
        await OnUIAsync(() =>
        {
            _disposed = true;
            _linkLifetime.Cancel();
            _launcher = null;
            LinkOpener = null;
            _sessionId = null;
            _ready.TrySetCanceled();
            Browser.StopLoading();
            Browser.OnDecidePolicy -= OnDecidePolicy;
            Browser.OnWebProcessTerminated -= OnWebProcessTerminated;
            WebKit.UserContentManager.ScriptMessageReceivedSignal.Disconnect(_content, _messageHandler);
            _content.UnregisterScriptMessageHandler(BridgeName, null);
            NavigationRequested = null; OperationFailed = null; LinkHovered = null; ContentProcessTerminated = null;
            releaseVisual();
            return Task.FromResult(true);
        });
    });
}
