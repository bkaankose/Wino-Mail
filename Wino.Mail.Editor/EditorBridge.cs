using Microsoft.Web.WebView2.Core;
using System.Text;
using System.Text.Json;
using Windows.Foundation;
using LegacyWebView = Windows.UI.Xaml.Controls.WebView;
using ModernWebView = Microsoft.UI.Xaml.Controls.WebView2;

namespace Wino.Mail.Editor;

internal sealed partial class EditorBridge : IDisposable
{
    private readonly ModernWebView? _modernWebView;
    private readonly LegacyWebView? _legacyWebView;
    private readonly TaskCompletionSource<bool> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _initialized;
    private bool _disposed;

    public EditorBridge(ModernWebView webView)
    {
        _modernWebView = webView ?? throw new ArgumentNullException(nameof(webView));
        ActiveMode = EditorWebViewMode.WebView2;
    }

    public EditorBridge(LegacyWebView webView)
    {
        _legacyWebView = webView ?? throw new ArgumentNullException(nameof(webView));
        ActiveMode = EditorWebViewMode.WebView;
    }

    public event EventHandler<EditorSelectionState>? SelectionStateChanged;
    public event EventHandler? ContentChanged;

    public EditorWebViewMode ActiveMode { get; }

    public async Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            await _ready.Task;
            return;
        }

        _initialized = true;
        if (ActiveMode == EditorWebViewMode.WebView2)
        {
            await InitializeWebView2Async();
        }
        else
        {
            await InitializeLegacyWebViewAsync();
        }
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

    public Task InsertImageAsync(string dataUri)
    {
        string dataUriJson = JsonSerializer.Serialize(dataUri, EditorJsonContext.Default.String);
        return ExecuteScriptAsync($"window.WinoEditor.insertImage({dataUriJson})");
    }

    public Task InsertTableAsync(int rows, int columns) =>
        ExecuteScriptAsync($"window.WinoEditor.insertTable({Math.Clamp(rows, 1, 20)}, {Math.Clamp(columns, 1, 20)})");

    public Task ExecuteTableCommandAsync(string command)
    {
        string commandJson = JsonSerializer.Serialize(command, EditorJsonContext.Default.String);
        return ExecuteScriptAsync($"window.WinoEditor.tableCommand({commandJson})");
    }

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
        return ActiveMode == EditorWebViewMode.WebView2
            ? JsonSerializer.Deserialize(result, EditorJsonContext.Default.String) ?? string.Empty
            : result;
    }

    public Task FocusAsync() => ExecuteScriptAsync("window.WinoEditor.focus()");

    public Task SetThemeAsync(bool isDarkMode)
    {
        if (_modernWebView?.CoreWebView2 is not null)
        {
            _modernWebView.CoreWebView2.Profile.PreferredColorScheme = isDarkMode
                ? CoreWebView2PreferredColorScheme.Dark
                : CoreWebView2PreferredColorScheme.Light;
        }

        return ExecuteScriptAsync($"window.WinoEditor.setTheme({isDarkMode.ToString().ToLowerInvariant()})");
    }

    public Task SetPasteAsHtmlAsync(bool enabled) => ExecuteScriptAsync(
        $"window.WinoEditor.setPasteAsHtml({enabled.ToString().ToLowerInvariant()})");

    public Task SetSpellCheckAsync(bool enabled) => ExecuteScriptAsync(
        $"window.WinoEditor.setSpellCheck({enabled.ToString().ToLowerInvariant()})");

    public Task SetParagraphStyleAsync(string tag) => ExecuteFunctionWithStringAsync("setParagraphStyle", tag);

    public Task SetLineHeightAsync(string value) => ExecuteFunctionWithStringAsync("setLineHeight", value);

    public Task InsertEmojiAsync(string value) => ExecuteFunctionWithStringAsync("insertEmoji", value);

    public Task<string> ExecuteScriptResultAsync(string script) => ExecuteScriptForResultAsync(script);

    public Task SetReadOnlyAsync(bool isReadOnly) => ExecuteScriptAsync(
        $"document.getElementById('wino-editor').contentEditable = '{(isReadOnly ? "false" : "true")}'");

    public void ReceiveLegacyMessage(string json)
    {
        if (ActiveMode == EditorWebViewMode.WebView)
        {
            ProcessMessage(json);
        }
    }

    public async Task ValidateLegacyNavigationAsync()
    {
        if (_legacyWebView is null) return;

        try
        {
            string status = await _legacyWebView.InvokeScriptAsync(
                "winoGetEditorStatus", null);
            if (status == "ready")
            {
                _ready.TrySetResult(true);
            }
            else
            {
                _ready.TrySetException(new InvalidOperationException(
                    $"Compatibility WebView loaded the document, but the editor did not initialize. {status}"));
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(new InvalidOperationException(
                $"Compatibility WebView could not call its bootstrap probe ({exception.HResult:X8}: {exception.Message}).",
                exception));
        }
    }

    private Task ExecuteFunctionWithStringAsync(string function, string value)
    {
        string valueJson = JsonSerializer.Serialize(value, EditorJsonContext.Default.String);
        return ExecuteScriptAsync($"window.WinoEditor.{function}({valueJson})");
    }

    private async Task InitializeWebView2Async()
    {
        string editorDocument = await EditorAssetProvider.GetEditorDocumentAsync();
        await AwaitAsync(_modernWebView!.EnsureCoreWebView2Async());
        ObjectDisposedException.ThrowIf(_disposed, this);
        _modernWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        _modernWebView.NavigateToString(editorDocument);
        await _ready.Task;
    }

    private async Task InitializeLegacyWebViewAsync()
    {
        string editorDocument = await EditorAssetProvider.GetLegacyEditorDocumentAsync();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _legacyWebView!.NavigateToString(editorDocument);
        await _ready.Task;
    }

    private async Task ExecuteScriptAsync(string script) =>
        _ = await ExecuteScriptForResultAsync(script);

    private async Task<string> ExecuteScriptForResultAsync(string script)
    {
        await EnsureReadyAsync();
        return ActiveMode == EditorWebViewMode.WebView2
            ? await _modernWebView!.ExecuteScriptAsync(script)
            : await _legacyWebView!.InvokeScriptAsync("eval", new string[] { script });
    }

    private async Task EnsureReadyAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            await InitializeAsync();
        }
        else
        {
            await _ready.Task;
        }
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args) =>
        ProcessMessage(args.WebMessageAsJson);

    private void ProcessMessage(string json)
    {
        EditorMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(json, EditorJsonContext.Default.EditorMessage);
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
                    $"Compatibility WebView JavaScript error: {message.Message ?? "Unknown script error."}"));
                break;
            case "selectionState" when message.State is not null:
                SelectionStateChanged?.Invoke(this, message.State);
                break;
            case "contentChanged":
                ContentChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private static async Task AwaitAsync(IAsyncAction action) => await action;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready.TrySetException(new ObjectDisposedException(nameof(EditorBridge)));
        if (_modernWebView?.CoreWebView2 is not null)
        {
            _modernWebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        }

        if (_modernWebView is not null)
        {
            try
            {
                _modernWebView.Close();
            }
            catch (InvalidOperationException)
            {
                // A failed runtime initialization can leave WebView2 without a controller.
            }
        }

        if (_legacyWebView is not null)
        {
            _legacyWebView.NavigateToString(string.Empty);
        }

    }
}
