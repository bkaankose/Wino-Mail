using Microsoft.Web.WebView2.Core;
using System.Text;
using System.Text.Json;
using ModernWebView = Microsoft.UI.Xaml.Controls.WebView2;

namespace Wino.Editor;

internal sealed partial class EditorBridge : IDisposable
{
    private static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(15);
    private readonly ModernWebView _webView;
    private readonly TaskCompletionSource<bool> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _initialized;
    private bool _disposed;

    public EditorBridge(ModernWebView webView) =>
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));

    public event EventHandler<EditorSelectionState>? SelectionStateChanged;
    public event EventHandler? ContentChanged;

    public async Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            await _ready.Task;
            return;
        }

        _initialized = true;
        string editorDocument = await EditorAssetProvider.GetEditorDocumentAsync();
        if (_webView.CoreWebView2 is null)
            throw new InvalidOperationException("WebView2 must be initialized before creating the editor bridge.");

        ObjectDisposedException.ThrowIf(_disposed, this);
        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.NavigateToString(editorDocument);
        await _ready.Task.WaitAsync(InitializationTimeout);
    }

    public Task ExecuteCommandAsync(string command, string? value = null)
    {
        string commandJson = JsonSerializer.Serialize(command, EditorJsonContext.Default.String);
        string valueJson = value is null ? "null" : JsonSerializer.Serialize(value, EditorJsonContext.Default.String);
        return ExecuteScriptAsync($"window.WinoEditor.exec({commandJson}, {valueJson})");
    }

    public Task CreateLinkAsync(string url) => CreateLinkAsync(url, null, true);

    public Task CreateLinkAsync(string url, string? text, bool openInNewWindow)
    {
        string urlJson = JsonSerializer.Serialize(url, EditorJsonContext.Default.String);
        string textJson = text is null ? "null" : JsonSerializer.Serialize(text, EditorJsonContext.Default.String);
        return ExecuteScriptAsync($"window.WinoEditor.createLink({urlJson}, {textJson}, {openInNewWindow.ToString().ToLowerInvariant()})");
    }

    public Task InsertImageAsync(string dataUri) =>
        ExecuteFunctionWithStringAsync("insertImage", dataUri);

    public Task InsertTableAsync(int rows, int columns) =>
        ExecuteScriptAsync($"window.WinoEditor.insertTable({Math.Clamp(rows, 1, 20)}, {Math.Clamp(columns, 1, 20)})");

    public Task ExecuteTableCommandAsync(string command) =>
        ExecuteFunctionWithStringAsync("tableCommand", command);

    public Task SetContentAsync(string html, string mode = "replace")
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(html ?? string.Empty));
        string encodedJson = JsonSerializer.Serialize(encoded, EditorJsonContext.Default.String);
        string modeJson = JsonSerializer.Serialize(mode, EditorJsonContext.Default.String);
        return ExecuteScriptAsync($"window.WinoEditor.setContent({encodedJson}, {modeJson})");
    }

    public async Task<string> GetContentAsync()
    {
        string result = await ExecuteScriptForResultAsync("window.WinoEditor.getContent()");
        return JsonSerializer.Deserialize(result, EditorJsonContext.Default.String) ?? string.Empty;
    }

    public async Task<string> GetBodyContentAsync()
    {
        string result = await ExecuteScriptForResultAsync("window.WinoEditor.getBodyContent()");
        return JsonSerializer.Deserialize(result, EditorJsonContext.Default.String) ?? string.Empty;
    }

    public Task FocusAsync() => ExecuteScriptAsync("window.WinoEditor.focus()");

    public Task SetThemeAsync(bool isDarkMode)
    {
        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.Profile.PreferredColorScheme = isDarkMode
                ? CoreWebView2PreferredColorScheme.Dark
                : CoreWebView2PreferredColorScheme.Light;
        }

        return ExecuteScriptAsync($"window.WinoEditor.setTheme({isDarkMode.ToString().ToLowerInvariant()})");
    }

    public Task SetTypographyAsync(string? fontFamily, int fontSize)
    {
        string fontJson = JsonSerializer.Serialize(fontFamily ?? "Segoe UI", EditorJsonContext.Default.String);
        return ExecuteScriptAsync($"window.WinoEditor.setTypography({fontJson}, {Math.Clamp(fontSize, 8, 72)})");
    }

    public Task SetPasteAsHtmlAsync(bool enabled) =>
        ExecuteScriptAsync($"window.WinoEditor.setPasteAsHtml({enabled.ToString().ToLowerInvariant()})");

    public Task SetSpellCheckAsync(bool enabled) =>
        ExecuteScriptAsync($"window.WinoEditor.setSpellCheck({enabled.ToString().ToLowerInvariant()})");

    public Task SetParagraphStyleAsync(string tag) => ExecuteFunctionWithStringAsync("setParagraphStyle", tag);
    public Task SetLineHeightAsync(string value) => ExecuteFunctionWithStringAsync("setLineHeight", value);
    public Task InsertEmojiAsync(string value) => ExecuteFunctionWithStringAsync("insertEmoji", value);
    public Task<string> ExecuteScriptResultAsync(string script) => ExecuteScriptForResultAsync(script);
    public Task SetReadOnlyAsync(bool isReadOnly) => ExecuteScriptAsync(
        $"document.getElementById('wino-editor').contentEditable = '{(isReadOnly ? "false" : "true")}'");

    private Task ExecuteFunctionWithStringAsync(string function, string value)
    {
        string valueJson = JsonSerializer.Serialize(value, EditorJsonContext.Default.String);
        return ExecuteScriptAsync($"window.WinoEditor.{function}({valueJson})");
    }

    private async Task ExecuteScriptAsync(string script) =>
        _ = await ExecuteScriptForResultAsync(script);

    private async Task<string> ExecuteScriptForResultAsync(string script)
    {
        await EnsureReadyAsync();
        return await _webView.ExecuteScriptAsync(script);
    }

    private async Task EnsureReadyAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) await InitializeAsync();
        else await _ready.Task;
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        EditorMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(args.WebMessageAsJson, EditorJsonContext.Default.EditorMessage);
        }
        catch (JsonException)
        {
            return;
        }

        switch (message?.Type)
        {
            case "ready":
                _ready.TrySetResult(true);
                break;
            case "bootstrapError":
                _ready.TrySetException(new InvalidOperationException(
                    $"Editor JavaScript error: {message.Message ?? "Unknown script error."}"));
                break;
            case "selectionState" when message.State is not null:
                SelectionStateChanged?.Invoke(this, message.State);
                break;
            case "contentChanged":
                ContentChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private async void OnNavigationCompleted(
        ModernWebView sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_ready.Task.IsCompleted || _disposed) return;

        if (!args.IsSuccess)
        {
            _ready.TrySetException(new InvalidOperationException(
                $"Editor navigation failed: {args.WebErrorStatus}."));
            return;
        }

        try
        {
            string result = await sender.ExecuteScriptAsync("typeof window.WinoEditor === 'object'");
            if (string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
                _ready.TrySetResult(true);
            else
                _ready.TrySetException(new InvalidOperationException(
                    "Editor document loaded, but editor.js did not initialize window.WinoEditor."));
        }
        catch (Exception exception)
        {
            _ready.TrySetException(new InvalidOperationException(
                "Editor document loaded, but its bootstrap probe failed.",
                exception));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready.TrySetException(new ObjectDisposedException(nameof(EditorBridge)));
        if (_webView.CoreWebView2 is not null)
            _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        _webView.NavigationCompleted -= OnNavigationCompleted;

        try { _webView.Close(); }
        catch (InvalidOperationException) { }
    }
}
