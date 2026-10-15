using System.Text;
using System.Text.Json;
using Android.Graphics;
using Android.OS;
using Android.Webkit;
using AndroidX.WebKit;
using Android.Runtime;
using Wino.Core.Domain.Interfaces;
using AndroidUri = Android.Net.Uri;
using WebView = Android.Webkit.WebView;

namespace Wino.Editor.Android;

/// <summary>
/// One WebView's document session (the Android twin of WebKitBrowserSession). Every load is a new generation: a fresh
/// reader or editor shell from the shared document builder (per-document script nonce and remote-content CSP) served by
/// <see cref="WebViewAssetLoader"/> from <c>https://appassets.androidplatform.net/wino/&lt;generation&gt;.html</c>, so the
/// page has a secure origin for the bridge and nothing else is served. Remote requests follow the generation's
/// <see cref="RemoteContentPolicy"/> in <c>shouldInterceptRequest</c> (blocked requests get an empty 403), every
/// navigation away from the shell is cancelled in <c>shouldOverrideUrlLoading</c> and reported as
/// <see cref="NavigationRequested"/>, and messages from the page arrive through a WebMessageListener named
/// <c>winoMail</c> (AndroidX WebKit feature check) or, on WebView builds without it, a JavaScript interface with the
/// same name. Only main-frame messages from the shell origin that carry the current generation are accepted. Native
/// state is touched on the main thread only.
/// </summary>
internal sealed class AndroidWebViewSession
{
    public const string Domain = "appassets.androidplatform.net";
    private const string Origin = "https://" + Domain;
    private const string ShellPath = "/wino/";

    private readonly Handler _main = new(Looper.MainLooper ?? throw new InvalidOperationException("The main looper is unavailable."));
    private readonly WebViewAssetLoader _assets;
    private readonly Client _client;
    private readonly bool _usesMessageListener;
    private readonly CancellationTokenSource _linkLifetime = new();
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _sessionId;
    private string? _document;
    private RemoteContentPolicy _policy = RemoteContentPolicy.Blocked;
    private IExternalLauncher? _launcher;
    private bool _disposed;

    public AndroidWebViewSession(WebView browser)
    {
        Browser = browser;
        _assets = new WebViewAssetLoader.Builder()
            .SetDomain(Domain)
            .AddPathHandler(ShellPath, new ShellPathHandler(this))
            .Build();
        _client = new Client(this);

        var settings = browser.Settings;
        settings.JavaScriptEnabled = true;
        settings.DomStorageEnabled = false;
        settings.AllowFileAccess = false;
        settings.AllowContentAccess = false;
        settings.JavaScriptCanOpenWindowsAutomatically = false;
        settings.SetSupportMultipleWindows(false);
        settings.SetGeolocationEnabled(false);
        settings.MediaPlaybackRequiresUserGesture = true;
        settings.MixedContentMode = MixedContentHandling.NeverAllow;
        settings.SaveFormData = false;
        // Follow the system font size (Settings > Display > Font size) like native text does.
        settings.TextZoom = (int)Math.Round(100 * (browser.Resources?.Configuration?.FontScale ?? 1f));
        browser.SetWebViewClient(_client);
        browser.SetWebChromeClient(new WebChromeClient());
        browser.SetBackgroundColor(Color.Transparent);

        if (WebViewFeature.IsFeatureSupported(WebViewFeature.WebMessageListener))
        {
            WebViewCompat.AddWebMessageListener(browser, "winoMail", new HashSet<string> { Origin }, new MessageListener(this));
            _usesMessageListener = true;
        }
        else
        {
            // Older WebView builds: the same object name through a JavaScript interface. It has no frame or origin
            // information, so the generation check below is the only gate.
            browser.AddJavascriptInterface(CreateJavaScriptBridge(new BridgeHandler(this)), "winoMail");
        }
    }

    public WebView Browser { get; }

    public SessionOperationQueue Queue { get; } = new();

    public bool IsReady => !_disposed && _sessionId is not null && _ready.Task.IsCompletedSuccessfully;

    public event EventHandler<EditorMessage>? Message;
    public event EventHandler<RendererNavigationRequestedEventArgs>? NavigationRequested;
    public event EventHandler<Exception>? OperationFailed;

    /// <summary>Links leave through <paramref name="launcher"/> (Custom Tabs, mail apps).</summary>
    public void Configure(IExternalLauncher launcher)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(launcher);
        _launcher = launcher;
    }

    /// <summary>Loads a fresh reader or editor shell and waits until its scripts report ready.</summary>
    public async Task LoadAsync(bool editor, RemoteContentPolicy policy, bool dark, CancellationToken token)
    {
        string id = Guid.NewGuid().ToString("N");
        string document = editor
            ? await AppleMailDocumentBuilder.BuildEditorAsync(id, dark, token, useCommandKey: false)
            : await AppleMailDocumentBuilder.BuildReaderAsync(id, policy, dark, token, useCommandKey: false);
        await OnUIAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sessionId = id;
            _document = document;
            _policy = policy;
            _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Browser.StopLoading();
            Browser.LoadUrl(Origin + ShellPath + id + ".html");
            return Task.CompletedTask;
        });
        try { await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30), token); }
        catch
        {
            await OnUIAsync(() => { _sessionId = null; _document = null; Browser.StopLoading(); return Task.CompletedTask; });
            throw;
        }
    }

    /// <summary>Evaluates <paramref name="expression"/> in the current generation and returns it as a string.</summary>
    public async Task<string?> EvaluateAsync(string expression)
    {
        if (!IsReady) throw new InvalidOperationException("The mail document is not ready.");
        string? generation = _sessionId;
        return await OnUIAsync(async () =>
        {
            var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Browser.EvaluateJavascript("String((" + expression + ") ?? '')", new ValueCallback(result =>
            {
                if (_disposed || generation != _sessionId) completion.TrySetException(new ObjectDisposedException(nameof(AndroidWebViewSession)));
                else completion.TrySetResult(DecodeScriptResult(result));
            }));
            return await completion.Task;
        });
    }

    /// <summary>evaluateJavascript returns the value as JSON; a string result is a JSON string literal.</summary>
    private static string? DecodeScriptResult(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "null") return null;
        try { return JsonSerializer.Deserialize(json, EditorJsonContext.Default.String); }
        catch (JsonException) { return json; }
    }

    public Task OnUIAsync(Func<Task> operation) => OnUIAsync(async () => { await operation(); return true; });

    public Task<T> OnUIAsync<T>(Func<Task<T>> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        async void Run()
        {
            try { completion.TrySetResult(await operation()); }
            catch (Exception error) { completion.TrySetException(error); }
        }
        if (Looper.MyLooper() == Looper.MainLooper) Run();
        else _main.Post(Run);
        return completion.Task;
    }

    private void Received(string? json, bool fromShell)
    {
        if (_disposed || !fromShell || string.IsNullOrEmpty(json)) return;
        try
        {
            var message = JsonSerializer.Deserialize(json, EditorJsonContext.Default.EditorMessage);
            if (_sessionId is null || message?.SessionId != _sessionId) return;
            if (message.Type == "ready") _ready.TrySetResult();
            else if (message.Type is "bootstrapError" or "initializationError")
                _ready.TrySetException(new InvalidOperationException("The mail document failed to initialize."));
            else if (message.Type is "openLink" or "navigation")
            {
                var reader = JsonSerializer.Deserialize(json, EditorJsonContext.Default.RendererMessage);
                if (Uri.TryCreate(message.Url ?? reader?.Uri, UriKind.Absolute, out var uri)) OpenLink(uri);
            }
            else Message?.Invoke(this, message);
        }
        catch (JsonException) { }
    }

    /// <summary>A link the page asked to open, or a navigation the client cancelled: http, https and mailto leave the app.</summary>
    private async void OpenLink(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https" or "mailto")) return;
        NavigationRequested?.Invoke(this, new(uri));
        if (_launcher is null || _linkLifetime.IsCancellationRequested) return;
        try { (await _launcher.LaunchUriAsync(uri, _linkLifetime.Token)).ThrowIfNotSucceeded(); }
        catch (Exception error) { if (!_linkLifetime.IsCancellationRequested) OperationFailed?.Invoke(this, error); }
    }

    private bool IsShellUrl(AndroidUri? url)
        => url is not null && url.Scheme == "https" && url.Host == Domain && url.Path == ShellPath + _sessionId + ".html";

    /// <summary>Serves the current generation's document; any other shell path is not found.</summary>
    private WebResourceResponse? ServeShell(string path)
    {
        if (_disposed || _sessionId is null || _document is null || path != _sessionId + ".html") return null;
        var bytes = Encoding.UTF8.GetBytes(_document);
        var response = new WebResourceResponse("text/html", "utf-8", new MemoryStream(bytes));
        response.ResponseHeaders = new Dictionary<string, string> { ["Cache-Control"] = "no-store" };
        return response;
    }

    private static WebResourceResponse Blocked()
        => new("text/plain", "utf-8", 403, "Blocked", new Dictionary<string, string>(), new MemoryStream([]));

    public ValueTask DisposeAsync(Action releaseVisual) => Queue.DisposeAsync(async () =>
    {
        await OnUIAsync(() =>
        {
            _disposed = true;
            _linkLifetime.Cancel();
            _launcher = null;
            _sessionId = null;
            _document = null;
            _ready.TrySetCanceled();
            Browser.StopLoading();
            if (_usesMessageListener)
            {
                try { WebViewCompat.RemoveWebMessageListener(Browser, "winoMail"); }
                catch (Java.Lang.Exception) { }
            }
            else Browser.RemoveJavascriptInterface("winoMail");
            Message = null; NavigationRequested = null; OperationFailed = null;
            releaseVisual();
            return Task.CompletedTask;
        });
    });

    private sealed class Client(AndroidWebViewSession owner) : WebViewClient
    {
        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
        {
            var url = request?.Url;
            if (url is null || owner._disposed) return Blocked();
            if (url.Host == Domain) return owner._assets.ShouldInterceptRequest(url) ?? Blocked();
            // Remote images and fonts only while the generation allows them; the CSP also limits what the page asks for.
            return owner._policy == RemoteContentPolicy.ImagesAndFontsAllowed && url.Scheme is "http" or "https" ? null : Blocked();
        }

        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            if (owner.IsShellUrl(request?.Url)) return false;
            if (request?.Url?.ToString() is { } address && Uri.TryCreate(address, UriKind.Absolute, out var uri) && request.HasGesture)
                owner.OpenLink(uri);
            return true;
        }

        public override void OnReceivedError(WebView? view, IWebResourceRequest? request, WebResourceError? error)
        {
            base.OnReceivedError(view, request, error);
            if (request?.IsForMainFrame == true && owner.IsShellUrl(request.Url))
                owner._ready.TrySetException(new InvalidOperationException($"The mail document failed to load: {error?.DescriptionFormatted}"));
        }
    }

    private sealed class ShellPathHandler(AndroidWebViewSession owner) : Java.Lang.Object, WebViewAssetLoader.IPathHandler
    {
        WebResourceResponse? WebViewAssetLoader.IPathHandler.Handle(string path) => owner.ServeShell(path);
    }

    private sealed class MessageListener(AndroidWebViewSession owner) : Java.Lang.Object, WebViewCompat.IWebMessageListener
    {
        public void OnPostMessage(WebView view, WebMessageCompat message, AndroidUri sourceOrigin, bool isMainFrame, JavaScriptReplyProxy replyProxy)
            => owner.Received(message.Data, isMainFrame && sourceOrigin.ToString() == Origin);
    }

    /// <summary>
    /// The Java bridge object (<c>Java/com/winomail/editor/WinoJavaScriptBridge.java</c>): its <c>postMessage</c> posts
    /// the JSON to <paramref name="handler"/> on the main thread. Created through JNI because the class is plain Java.
    /// </summary>
    private static Java.Lang.Object CreateJavaScriptBridge(Handler handler)
    {
        IntPtr type = JNIEnv.FindClass("com/winomail/editor/WinoJavaScriptBridge");
        try
        {
            IntPtr constructor = JNIEnv.GetMethodID(type, "<init>", "(Landroid/os/Handler;)V");
            IntPtr instance = JNIEnv.NewObject(type, constructor, new JValue(handler));
            return Java.Lang.Object.GetObject<Java.Lang.Object>(instance, JniHandleOwnership.TransferLocalRef)
                ?? throw new InvalidOperationException("The JavaScript bridge could not be created.");
        }
        finally { JNIEnv.DeleteGlobalRef(type); }
    }

    private sealed class BridgeHandler(AndroidWebViewSession owner)
        : Handler(Looper.MainLooper ?? throw new InvalidOperationException("The main looper is unavailable."))
    {
        // JavaScript interface calls arrive on a WebView background thread; the Java bridge posts them here.
        public override void HandleMessage(global::Android.OS.Message message) => owner.Received(message.Obj?.ToString(), fromShell: true);
    }

    private sealed class ValueCallback(Action<string?> callback) : Java.Lang.Object, IValueCallback
    {
        public void OnReceiveValue(Java.Lang.Object? value) => callback(value?.ToString());
    }
}
