using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Wino.Mail.Editor;

public sealed partial class WinoMailRenderer : UserControl, IDisposable
{
    public static readonly DependencyProperty WebViewModeProperty = DependencyProperty.Register(
        nameof(WebViewMode),
        typeof(EditorWebViewMode),
        typeof(WinoMailRenderer),
        new PropertyMetadata(EditorWebViewMode.Auto, OnWebViewModeChanged));

    public static readonly DependencyProperty LoadWebView2Property = DependencyProperty.Register(
        nameof(LoadWebView2),
        typeof(bool),
        typeof(WinoMailRenderer),
        new PropertyMetadata(false));

    public static readonly DependencyProperty LoadCompatibilityWebViewProperty = DependencyProperty.Register(
        nameof(LoadCompatibilityWebView),
        typeof(bool),
        typeof(WinoMailRenderer),
        new PropertyMetadata(false));

    private Task? _initializationTask;
    private readonly TaskCompletionSource<bool> _loadedSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<FrameworkElement>? _browserLoadedSource;
    private TaskCompletionSource<bool> _ready = CreateReadySource();
    private EditorWebViewMode _activeWebViewMode = EditorWebViewMode.Auto;
    private bool _isLoaded;
    private bool _isDarkMode;
    private bool _disposed;
    private string _originalHtml = string.Empty;
    private bool _shouldLinkify = true;
    private string _fontFamily = "Segoe UI";
    private int _fontSize = 15;
    private string _accessibilitySubject = string.Empty;
    private string _accessibilitySender = string.Empty;
    private string _accessibilityDate = string.Empty;
    private string _accessibilityBody = string.Empty;
    private int _contentVersion;
    private int _renderedContentVersion = -1;
    private bool _allowNextInternalNavigation;

    public WinoMailRenderer() => InitializeComponent();

    public event EventHandler<RendererNavigationRequestedEventArgs>? NavigationRequested;
    public event EventHandler<Exception>? InitializationFailed;

    public EditorWebViewMode WebViewMode
    {
        get => (EditorWebViewMode)GetValue(WebViewModeProperty);
        set => SetValue(WebViewModeProperty, value);
    }

    public bool LoadWebView2
    {
        get => (bool)GetValue(LoadWebView2Property);
        private set => SetValue(LoadWebView2Property, value);
    }

    public bool LoadCompatibilityWebView
    {
        get => (bool)GetValue(LoadCompatibilityWebViewProperty);
        private set => SetValue(LoadCompatibilityWebViewProperty, value);
    }

    public EditorWebViewMode ActiveWebViewMode => _activeWebViewMode;

    public bool IsDarkMode
    {
        get => _isDarkMode;
        set
        {
            if (_isDarkMode == value) return;
            _isDarkMode = value;
            if (_ready.Task.IsCompletedSuccessfully) _ = ApplyThemeAsync();
        }
    }

    public async Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _loadedSource.Task;
        while (true)
        {
            Task initializationTask = _initializationTask ?? QueueWebViewModeSwitch();
            await initializationTask;
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ReferenceEquals(initializationTask, _initializationTask)) return;
        }
    }

    public async Task RenderHtmlAsync(string html, bool shouldLinkify = true)
    {
        _originalHtml = html ?? string.Empty;
        _shouldLinkify = shouldLinkify;
        _contentVersion++;
        await InitializeAsync();
        await RenderPendingHtmlAsync();
    }

    public Task RenderPlainTextAsync(string text, bool shouldLinkify = true)
    {
        string encodedText = System.Net.WebUtility.HtmlEncode(text ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace("\n", "<br>");
        return RenderHtmlAsync(encodedText, shouldLinkify);
    }

    public Task<string> GetOriginalHtmlAsync() => Task.FromResult(_originalHtml);

    public async Task ClearAsync()
    {
        _originalHtml = string.Empty;
        _contentVersion++;
        await InitializeAsync();
        await ExecuteDirectAsync("window.WinoRenderer.clear()");
        _renderedContentVersion = _contentVersion;
    }

    public async Task SetReaderTypographyAsync(string? fontFamily, int fontSize)
    {
        _fontFamily = fontFamily ?? "Segoe UI";
        _fontSize = Math.Clamp(fontSize, 8, 72);
        await InitializeAsync();
        await ApplyTypographyAsync();
    }

    public async Task SetAccessibilityContextAsync(
        string? subject,
        string? sender,
        string? date,
        string? bodyContext = null)
    {
        _accessibilitySubject = subject ?? string.Empty;
        _accessibilitySender = sender ?? string.Empty;
        _accessibilityDate = date ?? string.Empty;
        _accessibilityBody = bodyContext ?? string.Empty;
        await InitializeAsync();
        await ApplyAccessibilityAsync();
    }

    public Microsoft.UI.Xaml.Controls.WebView2 GetUnderlyingWebView() =>
        _activeWebViewMode == EditorWebViewMode.WebView2 && RendererWebView2 is not null
            ? RendererWebView2
            : throw new InvalidOperationException("The renderer is not currently using WebView2.");

    public WebView GetUnderlyingCompatibilityWebView() =>
        _activeWebViewMode == EditorWebViewMode.WebView && CompatibilityRendererWebView is not null
            ? CompatibilityRendererWebView
            : throw new InvalidOperationException("The renderer is not currently using the compatibility WebView.");

    private Task QueueWebViewModeSwitch()
    {
        Task previousTask = _initializationTask ?? Task.CompletedTask;
        _initializationTask = SwitchWebViewModeAfterAsync(previousTask);
        return _initializationTask;
    }

    private async Task SwitchWebViewModeAfterAsync(Task previousTask)
    {
        try
        {
            await previousTask;
        }
        catch
        {
            // A new mode request gets a clean attempt after an earlier failure.
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        await SwitchWebViewModeAsync(WebViewMode);
    }

    private async Task SwitchWebViewModeAsync(EditorWebViewMode requestedMode)
    {
        EditorWebViewMode resolvedMode = EditorWebViewModeResolver.Resolve(requestedMode);
        if (_activeWebViewMode == resolvedMode && _ready.Task.IsCompletedSuccessfully) return;

        DisposeBrowser();
        UnloadBrowserHosts();
        _ready = CreateReadySource();
        _renderedContentVersion = -1;

        try
        {
            await LoadBrowserAsync(resolvedMode);
        }
        catch (Exception) when (
            requestedMode == EditorWebViewMode.Auto &&
            resolvedMode == EditorWebViewMode.WebView2 &&
            !_disposed)
        {
            DisposeBrowser();
            UnloadBrowserHosts();
            _ready = CreateReadySource();
            await LoadBrowserAsync(EditorWebViewMode.WebView);
        }

        await ApplyThemeAsync();
        await ApplyTypographyAsync();
        await ApplyAccessibilityAsync();
        await RenderPendingHtmlAsync();
    }

    private async Task LoadBrowserAsync(EditorWebViewMode mode)
    {
        _browserLoadedSource = new TaskCompletionSource<FrameworkElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (mode == EditorWebViewMode.WebView2) LoadWebView2 = true;
        else LoadCompatibilityWebView = true;

        FrameworkElement browser = await _browserLoadedSource.Task;
        ObjectDisposedException.ThrowIf(_disposed, this);
        _activeWebViewMode = mode;

        try
        {
            if (mode == EditorWebViewMode.WebView2)
            {
                var webView = (Microsoft.UI.Xaml.Controls.WebView2)browser;
                string document = await EditorAssetProvider.GetReaderWebView2DocumentAsync();
                await AwaitAsync(webView.EnsureCoreWebView2Async());
                ObjectDisposedException.ThrowIf(_disposed, this);
                webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                webView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
                _allowNextInternalNavigation = true;
                try
                {
                    webView.NavigateToString(document);
                }
                catch
                {
                    _allowNextInternalNavigation = false;
                    throw;
                }
            }
            else
            {
                string document = await EditorAssetProvider.GetLegacyReaderDocumentAsync();
                _allowNextInternalNavigation = true;
                try
                {
                    ((WebView)browser).NavigateToString(document);
                }
                catch
                {
                    _allowNextInternalNavigation = false;
                    throw;
                }
            }

            await _ready.Task;
        }
        finally
        {
            _browserLoadedSource = null;
        }
    }

    private async Task RenderPendingHtmlAsync()
    {
        int version = _contentVersion;
        if (_renderedContentVersion == version) return;
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(_originalHtml));
        string encodedJson = JsonSerializer.Serialize(encoded, EditorJsonContext.Default.String);
        await ExecuteDirectAsync(
            $"window.WinoRenderer.render({encodedJson}, {_shouldLinkify.ToString().ToLowerInvariant()})");
        if (version == _contentVersion) _renderedContentVersion = version;
    }

    private Task ApplyThemeAsync()
    {
        if (_activeWebViewMode == EditorWebViewMode.WebView2 && RendererWebView2?.CoreWebView2 is not null)
        {
            RendererWebView2.CoreWebView2.Profile.PreferredColorScheme = IsDarkMode
                ? CoreWebView2PreferredColorScheme.Dark
                : CoreWebView2PreferredColorScheme.Light;
        }

        return ExecuteDirectAsync(
            $"window.WinoRenderer.setTheme({IsDarkMode.ToString().ToLowerInvariant()})");
    }

    private Task ApplyTypographyAsync()
    {
        string fontJson = JsonSerializer.Serialize(_fontFamily, EditorJsonContext.Default.String);
        return ExecuteDirectAsync($"window.WinoRenderer.setTypography({fontJson}, {_fontSize})");
    }

    private Task ApplyAccessibilityAsync()
    {
        string subjectJson = JsonSerializer.Serialize(_accessibilitySubject, EditorJsonContext.Default.String);
        string senderJson = JsonSerializer.Serialize(_accessibilitySender, EditorJsonContext.Default.String);
        string dateJson = JsonSerializer.Serialize(_accessibilityDate, EditorJsonContext.Default.String);
        string bodyJson = JsonSerializer.Serialize(_accessibilityBody, EditorJsonContext.Default.String);
        return ExecuteDirectAsync(
            $"window.WinoRenderer.setAccessibility({subjectJson}, {senderJson}, {dateJson}, {bodyJson})");
    }

    private async Task<string> ExecuteDirectAsync(string script)
    {
        await _ready.Task;
        return _activeWebViewMode == EditorWebViewMode.WebView2
            ? await RendererWebView2.ExecuteScriptAsync(script)
            : await CompatibilityRendererWebView.InvokeScriptAsync(
                "eval",
                new string[] { script });
    }

    private void ProcessMessage(string json)
    {
        RendererMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(json, EditorJsonContext.Default.RendererMessage);
        }
        catch (JsonException)
        {
            return;
        }

        if (message?.Type == "ready")
        {
            _ready.TrySetResult(true);
        }
        else if (message?.Type == "navigation" &&
                 Uri.TryCreate(message.Uri, UriKind.Absolute, out Uri? uri))
        {
            NavigationRequested?.Invoke(this, new RendererNavigationRequestedEventArgs(uri));
        }
    }

    private void CoreWebView2_WebMessageReceived(
        CoreWebView2 sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_activeWebViewMode == EditorWebViewMode.WebView2)
            ProcessMessage(args.WebMessageAsJson);
    }

    private void CoreWebView2_NewWindowRequested(
        CoreWebView2 sender,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        RequestNavigation(args.Uri);
    }

    private void RendererWebView2_Loaded(object sender, RoutedEventArgs e) =>
        _browserLoadedSource?.TrySetResult((FrameworkElement)sender);

    private void CompatibilityRendererWebView_Loaded(object sender, RoutedEventArgs e) =>
        _browserLoadedSource?.TrySetResult((FrameworkElement)sender);

    private async void RendererWebView2_NavigationCompleted(
        Microsoft.UI.Xaml.Controls.WebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_activeWebViewMode != EditorWebViewMode.WebView2 ||
            !ReferenceEquals(sender, RendererWebView2)) return;
        if (_ready.Task.IsCompleted) return;
        if (!args.IsSuccess)
        {
            _ready.TrySetException(new InvalidOperationException(
                $"Renderer navigation failed: {args.WebErrorStatus}."));
            return;
        }

        try
        {
            string result = await sender.ExecuteScriptAsync(
                "typeof window.WinoRenderer === 'object'");
            if (string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
                _ready.TrySetResult(true);
            else
                _ready.TrySetException(new InvalidOperationException(
                    "Renderer document loaded, but reader.js did not initialize window.WinoRenderer."));
        }
        catch (Exception exception)
        {
            _ready.TrySetException(new InvalidOperationException(
                "Renderer document loaded, but its bootstrap probe failed.",
                exception));
        }
    }

    private async void CompatibilityRendererWebView_NavigationCompleted(
        WebView sender,
        WebViewNavigationCompletedEventArgs args)
    {
        if (_activeWebViewMode != EditorWebViewMode.WebView ||
            !ReferenceEquals(sender, CompatibilityRendererWebView)) return;
        if (_ready.Task.IsCompleted) return;
        if (!args.IsSuccess)
        {
            _ready.TrySetException(new InvalidOperationException(
                $"Compatibility renderer navigation failed: {args.WebErrorStatus}."));
            return;
        }

        try
        {
            string status = await sender.InvokeScriptAsync("winoGetRendererStatus", null);
            if (status == "ready") _ready.TrySetResult(true);
            else _ready.TrySetException(new InvalidOperationException(
                $"Compatibility renderer did not initialize. {status}"));
        }
        catch (Exception exception)
        {
            _ready.TrySetException(new InvalidOperationException(
                "Compatibility renderer bootstrap probe failed.",
                exception));
        }
    }

    private void RendererWebView2_NavigationStarting(
        Microsoft.UI.Xaml.Controls.WebView2 sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (_allowNextInternalNavigation)
        {
            _allowNextInternalNavigation = false;
            return;
        }

        if (string.IsNullOrEmpty(args.Uri) ||
            string.Equals(args.Uri, "about:blank", StringComparison.OrdinalIgnoreCase)) return;
        args.Cancel = true;
        RequestNavigation(args.Uri);
    }

    private void CompatibilityRendererWebView_NavigationStarting(
        WebView sender,
        WebViewNavigationStartingEventArgs args)
    {
        if (_allowNextInternalNavigation)
        {
            _allowNextInternalNavigation = false;
            return;
        }

        if (args.Uri is null ||
            string.Equals(args.Uri.AbsoluteUri, "about:blank", StringComparison.OrdinalIgnoreCase)) return;
        args.Cancel = true;
        RequestNavigation(args.Uri.AbsoluteUri);
    }

    private void CompatibilityRendererWebView_ScriptNotify(object sender, NotifyEventArgs e)
    {
        if (_activeWebViewMode == EditorWebViewMode.WebView &&
            ReferenceEquals(sender, CompatibilityRendererWebView))
            ProcessMessage(e.Value);
    }

    private void RequestNavigation(string? value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            NavigationRequested?.Invoke(this, new RendererNavigationRequestedEventArgs(uri));
    }

    private static void OnWebViewModeChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        var renderer = (WinoMailRenderer)sender;
        if (!renderer._disposed && renderer._isLoaded)
            renderer.ObserveModeSwitch(renderer.QueueWebViewModeSwitch());
    }

    private async void ObserveModeSwitch(Task switchTask)
    {
        try
        {
            await switchTask;
        }
        catch (Exception exception) when (ReferenceEquals(switchTask, _initializationTask))
        {
            InitializationFailed?.Invoke(this, exception);
        }
        catch
        {
            // A newer mode switch owns the renderer state.
        }
    }

    private async void WinoMailRenderer_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        _loadedSource.TrySetResult(true);
        if (_activeWebViewMode != EditorWebViewModeResolver.Resolve(WebViewMode))
            QueueWebViewModeSwitch();
        try
        {
            await InitializeAsync();
        }
        catch (Exception exception)
        {
            InitializationFailed?.Invoke(this, exception);
        }
    }

    private void WinoMailRenderer_Unloaded(object sender, RoutedEventArgs e) =>
        _isLoaded = false;

    private void UnloadBrowserHosts()
    {
        LoadWebView2 = false;
        LoadCompatibilityWebView = false;
    }

    private void DisposeBrowser()
    {
        _allowNextInternalNavigation = false;

        if (RendererWebView2?.CoreWebView2 is not null)
        {
            RendererWebView2.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
            RendererWebView2.CoreWebView2.NewWindowRequested -= CoreWebView2_NewWindowRequested;
        }

        if (RendererWebView2 is not null)
        {
            try { RendererWebView2.Close(); }
            catch (InvalidOperationException) { }
        }

        _activeWebViewMode = EditorWebViewMode.Auto;
    }

    private static TaskCompletionSource<bool> CreateReadySource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task AwaitAsync(IAsyncAction action) => await action;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loadedSource.TrySetException(new ObjectDisposedException(nameof(WinoMailRenderer)));
        _browserLoadedSource?.TrySetException(new ObjectDisposedException(nameof(WinoMailRenderer)));
        _ready.TrySetException(new ObjectDisposedException(nameof(WinoMailRenderer)));
        DisposeBrowser();
        UnloadBrowserHosts();
        GC.SuppressFinalize(this);
    }
}

public sealed record RendererNavigationRequestedEventArgs(Uri Uri);

public sealed record RendererMessage
{
    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string? Type { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("uri")]
    public string? Uri { get; init; }
}
