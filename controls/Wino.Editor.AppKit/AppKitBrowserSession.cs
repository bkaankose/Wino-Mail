using Foundation;
using WebKit;
using System.Text.Json;
using Wino.Core.Domain.Interfaces;

namespace Wino.Editor.AppKit;

// Owns the native delegates and one current document generation. Access native state on the UI thread.
internal sealed class AppKitBrowserSession : NSObject, IWKScriptMessageHandler
{
    private readonly NavigationGuard _navigation = new();
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _sessionId;
    private bool _disposed;
    private IExternalLauncher? _launcher;
    private readonly CancellationTokenSource _linkLifetime = new();
    public WKWebView Browser { get; }
    public SessionOperationQueue Queue { get; } = new();
    public bool IsReady => !_disposed && _sessionId is not null && _ready.Task.IsCompletedSuccessfully;
    public event EventHandler<EditorMessage>? Message;
    public event EventHandler<RendererNavigationRequestedEventArgs>? NavigationRequested;
    public event EventHandler<Exception>? OperationFailed;
    public AppKitBrowserSession(WKWebView browser)
    {
        Browser = browser;
        browser.Configuration.UserContentController.AddScriptMessageHandler(this, "winoMail");
        browser.NavigationDelegate = _navigation;
    }
    public void Configure(IExternalLauncher launcher)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(launcher);
        _launcher = launcher;
    }
    public async Task LoadAsync(bool editor, RemoteContentPolicy policy, bool dark, CancellationToken token)
    {
        string id = Guid.NewGuid().ToString("N");
        string document = editor ? await MacOsMailDocumentBuilder.BuildEditorAsync(id, dark, token)
            : await MacOsMailDocumentBuilder.BuildReaderAsync(id, policy, dark, token);
        await OnUIAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sessionId = id;
            _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _navigation.AllowShell = true;
            Browser.StopLoading();
            Browser.LoadHtmlString(document, null);
            return Task.CompletedTask;
        });
        try { await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30), token); }
        catch
        {
            await OnUIAsync(() => { _sessionId = null; Browser.StopLoading(); return Task.CompletedTask; });
            throw;
        }
    }
    public async Task<string?> EvaluateAsync(string expression)
    {
        if (!IsReady) throw new InvalidOperationException("The mail document is not ready.");
        string? generation = _sessionId;
        return await OnUIAsync(async () =>
        {
            var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Browser.EvaluateJavaScript("String((" + expression + ") ?? '')", (result, error) =>
            {
                if (_disposed || generation != _sessionId) completion.TrySetException(new ObjectDisposedException(nameof(AppKitBrowserSession)));
                else if (error is not null) completion.TrySetException(new NSErrorException(error));
                else completion.TrySetResult(result?.ToString());
            });
            return await completion.Task;
        });
    }
    public Task OnUIAsync(Func<Task> operation) => OnUIAsync(async () => { await operation(); return true; });
    public Task<T> OnUIAsync<T>(Func<Task<T>> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Browser.BeginInvokeOnMainThread(async () =>
        {
            try { completion.TrySetResult(await operation()); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        return completion.Task;
    }
    public void DidReceiveScriptMessage(WKUserContentController controller, WKScriptMessage nativeMessage)
    {
        if (_disposed || !nativeMessage.FrameInfo.MainFrame) return;
        string? source = nativeMessage.FrameInfo.Request.Url?.AbsoluteString;
        if (source is not (null or "about:blank")) return;
        try
        {
            string json = nativeMessage.Body.ToString();
            var message = JsonSerializer.Deserialize(json, EditorJsonContext.Default.EditorMessage);
            if (_sessionId is null || message?.SessionId != _sessionId) return;
            if (message.Type == "ready") _ready.TrySetResult();
            else if (message.Type is "bootstrapError" or "initializationError")
                _ready.TrySetException(new InvalidOperationException("The mail document failed to initialize."));
            else if (message.Type is "openLink" or "navigation")
            {
                var reader = JsonSerializer.Deserialize(json, EditorJsonContext.Default.RendererMessage);
                if (Uri.TryCreate(message.Url ?? reader?.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
                    OpenLink(uri);
            }
            else Message?.Invoke(this, message);
        }
        catch (JsonException) { }
    }
    private async void OpenLink(Uri uri)
    {
        NavigationRequested?.Invoke(this, new(uri));
        if (_launcher is null || _linkLifetime.IsCancellationRequested) return;
        try { (await _launcher.LaunchUriAsync(uri, _linkLifetime.Token)).ThrowIfNotSucceeded(); }
        catch (Exception error) { if (!_linkLifetime.IsCancellationRequested) OperationFailed?.Invoke(this, error); }
    }
    public ValueTask DisposeAsync(Action releaseVisual) => Queue.DisposeAsync(async () =>
    {
        await OnUIAsync(() =>
        {
            _disposed = true;
            _linkLifetime.Cancel();
            _launcher = null;
            _sessionId = null;
            _ready.TrySetCanceled();
            Browser.StopLoading();
            Browser.Configuration.UserContentController.RemoveScriptMessageHandler("winoMail");
            Browser.NavigationDelegate = null;
            Message = null; NavigationRequested = null; OperationFailed = null;
            releaseVisual();
            _navigation.Dispose();
            return Task.CompletedTask;
        });
    });
    private sealed class NavigationGuard : WKNavigationDelegate
    {
        public bool AllowShell { get; set; }
        public override void DecidePolicy(WKWebView webView, WKNavigationAction action, Action<WKNavigationActionPolicy> decisionHandler)
        {
            bool allow = AllowShell && action.Request.Url?.AbsoluteString == "about:blank";
            AllowShell = false;
            decisionHandler(allow ? WKNavigationActionPolicy.Allow : WKNavigationActionPolicy.Cancel);
        }
    }
}
