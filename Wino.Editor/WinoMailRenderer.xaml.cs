using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Wino.Editor;

public sealed partial class WinoMailRenderer : UserControl, IHtmlMailRenderer
{
    private static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(15);
    private Task? _initializationTask;
    private readonly TaskCompletionSource<bool> _loadedSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> _ready = CreateReadySource();
    private bool _isDarkMode;
    private bool _disposed;
    private string _originalHtml = string.Empty;
    private bool _shouldLinkify = true;
    private string _fontFamily = "Segoe UI";
    private int _fontSize = 15;
    private string _accessibilitySubject = string.Empty;
    private string _accessibilitySender = string.Empty;
    private string _accessibilityDate = string.Empty;
    private string _accessibilityBodyName = "Message body";
    private string _accessibilityFallbackName = "Plain text message";
    private string _accessibilityText = string.Empty;
    private int _contentVersion;
    private int _renderedContentVersion = -1;
    private bool _allowNextInternalNavigation;

    public WinoMailRenderer() => InitializeComponent();

    public event EventHandler<RendererNavigationRequestedEventArgs>? NavigationRequested;
    public event EventHandler<Exception>? InitializationFailed;

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

    /// <summary>
    /// Optional host-owned environment. Set this before the control is loaded to share
    /// Wino Mail's WebView2 profile, options, and process pool.
    /// </summary>
    public CoreWebView2Environment? WebViewEnvironment { get; set; }

    public async Task InitializeAsync()
    {
        //ObjectDisposedException.ThrowIf(_disposed, this);
        await _loadedSource.Task;
        _initializationTask ??= InitializeCoreAsync();
        await _initializationTask;
        // ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async Task RenderHtmlAsync(string html, bool shouldLinkify = true)
    {
        _originalHtml = html ?? string.Empty;
        _shouldLinkify = shouldLinkify;
        _contentVersion++;
        await InitializeAsync();
        await RenderPendingHtmlAsync();
    }

    public async Task SetThemeAsync(bool isDarkMode)
    {
        bool wasReady = _ready.Task.IsCompletedSuccessfully;
        bool themeChanged = _isDarkMode != isDarkMode;
        _isDarkMode = isDarkMode;

        await InitializeAsync();

        // Initialization applies the preloaded theme itself. Only an already-running
        // document needs an explicit update.
        if (wasReady && themeChanged) await ApplyThemeAsync();
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
        => await SetAccessibilityContextAsync(
            subject,
            sender,
            date,
            "Message body",
            "Plain text message",
            bodyContext);

    public async Task SetAccessibilityContextAsync(
        string? subject,
        string? sender,
        string? date,
        string? bodyAutomationName,
        string? plainTextFallbackAutomationName,
        string? accessibleText)
    {
        _accessibilitySubject = subject ?? string.Empty;
        _accessibilitySender = sender ?? string.Empty;
        _accessibilityDate = date ?? string.Empty;
        _accessibilityBodyName = bodyAutomationName ?? "Message body";
        _accessibilityFallbackName = plainTextFallbackAutomationName ?? "Plain text message";
        _accessibilityText = accessibleText ?? string.Empty;
        await InitializeAsync();
        await ApplyAccessibilityAsync();
    }

    public Microsoft.UI.Xaml.Controls.WebView2 GetUnderlyingWebView() =>
        !_disposed && RendererWebView2 is not null
            ? RendererWebView2
            : throw new ObjectDisposedException(nameof(WinoMailRenderer));

    public void SetMemoryUsageTargetLevel(CoreWebView2MemoryUsageTargetLevel level)
    {
        if (!_disposed && RendererWebView2?.CoreWebView2 is not null)
            RendererWebView2.CoreWebView2.MemoryUsageTargetLevel = level;
    }

    private async Task InitializeCoreAsync()
    {
        string document = await EditorAssetProvider.GetReaderDocumentAsync(IsDarkMode);
        var environment = WebViewEnvironment ?? await WinoWebViewEnvironment.GetSharedEnvironmentAsync();
        await RendererWebView2.EnsureCoreWebView2Async(environment);
        ObjectDisposedException.ThrowIf(_disposed, this);
        RendererWebView2.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
        RendererWebView2.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
        _allowNextInternalNavigation = true;
        try
        {
            RendererWebView2.NavigateToString(document);
        }
        catch
        {
            _allowNextInternalNavigation = false;
            throw;
        }

        await _ready.Task.WaitAsync(InitializationTimeout);

        await ApplyThemeAsync();
        await ApplyTypographyAsync();
        await ApplyAccessibilityAsync();
        await RenderPendingHtmlAsync();
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
        if (RendererWebView2?.CoreWebView2 is not null)
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
        string bodyNameJson = JsonSerializer.Serialize(_accessibilityBodyName, EditorJsonContext.Default.String);
        string fallbackNameJson = JsonSerializer.Serialize(_accessibilityFallbackName, EditorJsonContext.Default.String);
        string accessibleTextJson = JsonSerializer.Serialize(_accessibilityText, EditorJsonContext.Default.String);
        return ExecuteDirectAsync(
            $"window.WinoRenderer.setAccessibility({subjectJson}, {senderJson}, {dateJson}, {bodyNameJson}, {fallbackNameJson}, {accessibleTextJson})");
    }

    private async Task<string> ExecuteDirectAsync(string script)
    {
        await _ready.Task;
        return await RendererWebView2.ExecuteScriptAsync(script);
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
        => ProcessMessage(args.WebMessageAsJson);

    private void CoreWebView2_NewWindowRequested(
        CoreWebView2 sender,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        RequestNavigation(args.Uri);
    }

    private async void RendererWebView2_NavigationCompleted(
        Microsoft.UI.Xaml.Controls.WebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!ReferenceEquals(sender, RendererWebView2)) return;
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

    private void RequestNavigation(string? value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            NavigationRequested?.Invoke(this, new RendererNavigationRequestedEventArgs(uri));
    }

    private async void WinoMailRenderer_Loaded(object sender, RoutedEventArgs e)
    {
        _loadedSource.TrySetResult(true);
        try
        {
            await InitializeAsync();
            SetMemoryUsageTargetLevel(CoreWebView2MemoryUsageTargetLevel.Normal);
        }
        catch (Exception exception)
        {
            InitializationFailed?.Invoke(this, exception);
        }
    }

    private void WinoMailRenderer_Unloaded(object sender, RoutedEventArgs e) =>
        SetMemoryUsageTargetLevel(CoreWebView2MemoryUsageTargetLevel.Low);

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

    }

    private static TaskCompletionSource<bool> CreateReadySource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loadedSource.TrySetException(new ObjectDisposedException(nameof(WinoMailRenderer)));
        _ready.TrySetException(new ObjectDisposedException(nameof(WinoMailRenderer)));
        DisposeBrowser();
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
