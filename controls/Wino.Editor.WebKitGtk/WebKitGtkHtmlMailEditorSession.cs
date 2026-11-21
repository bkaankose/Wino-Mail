using System.Text;
using System.Text.Json;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using static Wino.Editor.WebKitGtk.EditorCommandScripts;

namespace Wino.Editor.WebKitGtk;

/// <summary>
/// The HTML mail editor in a WebKitGTK 6.0 web view: the Linux twin of <c>WebKitHtmlMailEditorSession</c>. Create it on
/// the GTK main thread and place <see cref="View"/> in the window; events are raised on the main thread.
/// </summary>
public sealed class WebKitGtkHtmlMailEditorSession : IHtmlMailEditorSession
{
    private static readonly HashSet<string> TableCommands = new(StringComparer.Ordinal) { "addRow", "removeRow", "addColumn", "removeColumn", "deleteTable" };
    private readonly WebKitGtkBrowserSession _host;
    private bool _dark;
    private RemoteContentPolicy _policy = RemoteContentPolicy.Blocked;
    private RemoteContentPolicy _loadedPolicy = RemoteContentPolicy.Blocked;
    private string? _openLinkLabel;
    private string? _removeLinkLabel;
    private bool _disposed;

    public WebKitGtkHtmlMailEditorSession(IDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _host = new(dispatcher);
        _host.Message += MessageReceived;
        _host.OperationFailed += (_, e) => OperationFailed?.Invoke(this, e);
        _host.ContentProcessTerminated += ContentProcessTerminated;
    }

    public WebKit.WebView View => _host.Browser;

    /// <summary>
    /// True from a web process loss until the head renders the draft again. The reloaded editor is empty, so reading the
    /// body in that window would save an empty draft over the real one.
    /// </summary>
    public bool IsContentLost { get; private set; }
    public bool IsReady => !_disposed && _host.IsReady;
    public bool IsInTable { get; private set; }
    public EditorState CurrentState { get; private set; } = new();
    public EditorCommandCapabilities Capabilities { get; } = new()
    {
        Fonts = (string[])["Arial", "Helvetica", "Times New Roman", "Courier New"],
        FontSizes = (int[])[8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 48, 72],
        Alignments = (EditorTextAlignment[])[EditorTextAlignment.Left, EditorTextAlignment.Center, EditorTextAlignment.Right, EditorTextAlignment.Justify],
        ParagraphStyles = (EditorParagraphStyleOption[])[new("Paragraph", "p"), new("Heading 1", "h1"), new("Heading 2", "h2"), new("Heading 3", "h3"), new("Quote", "blockquote"), new("Preformatted", "pre"), new("Code", "code")],
        LineHeights = (string[])["normal", "1", "1.15", "1.5", "2"]
    };

    public event EventHandler? ContentChanged;
    public event EventHandler<EditorState>? StateChanged;
    public event EventHandler<EditorShortcutKind>? ShortcutRequested;
    public event EventHandler<EditorApplicationShortcutGesture>? ApplicationShortcutRequested;
    public event EventHandler? ImageInsertionRequested;
    public event EventHandler<Exception>? OperationFailed;

    /// <summary>The editor was reloaded empty after its web process ended; the head renders the draft again.</summary>
    public event EventHandler? Restarted;

    public void Configure(IExternalLauncher launcher) => _host.Configure(launcher);

    public Func<Uri, string?, Task<PlatformOperationResult>>? LinkOpener
    {
        get => _host.LinkOpener;
        set => _host.LinkOpener = value;
    }

    private async void ContentProcessTerminated(object? sender, EventArgs e)
    {
        IsContentLost = true;
        try
        {
            await _host.Queue.RunAsync(EnsureReadyAsync, CancellationToken.None);
            Restarted?.Invoke(this, EventArgs.Empty);
        }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (Exception error) { OperationFailed?.Invoke(this, error); }
    }

    // The editor document has no initial data-theme and follows prefers-color-scheme until setTheme runs.
    private async Task EnsureReadyAsync(CancellationToken token)
    {
        if (_host.IsReady && _loadedPolicy == _policy) return;
        var policy = _policy;
        await _host.LoadAsync(true, policy, _dark, token);
        _loadedPolicy = policy;
        await _host.EvaluateAsync($"window.WinoEditor.setTheme({Bool(_dark)})");
        await ApplyLinkLabelsAsync();
    }

    private Task ApplyLinkLabelsAsync() => _openLinkLabel is null || _removeLinkLabel is null
        ? Task.CompletedTask
        : _host.EvaluateAsync($"window.WinoEditor.setLinkLabels({Quote(_openLinkLabel)}, {Quote(_removeLinkLabel)})");

    private Task RunAsync(Func<Task> operation, CancellationToken token) => _host.Queue.RunAsync(async lifetime =>
    {
        await EnsureReadyAsync(lifetime);
        await operation();
    }, token);

    private Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken token) => _host.Queue.RunAsync(async lifetime =>
    {
        await EnsureReadyAsync(lifetime);
        return await operation();
    }, token);

    public Task InitializeAsync(CancellationToken cancellationToken = default) => _host.Queue.RunAsync(EnsureReadyAsync, cancellationToken);

    /// <summary>The policy is part of the document's CSP, so a change reloads the editor shell; call it before the body.</summary>
    public Task SetRemoteContentPolicyAsync(RemoteContentPolicy policy, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        return _host.Queue.RunAsync(async token =>
        {
            _policy = policy;
            if (_host.IsReady) await EnsureReadyAsync(token);
        }, cancellationToken);
    }

    public Task SetLinkLabelsAsync(string openLabel, string removeLabel, CancellationToken cancellationToken = default) =>
        _host.Queue.RunAsync(async () =>
        {
            _openLinkLabel = openLabel;
            _removeLinkLabel = removeLabel;
            if (_host.IsReady) await ApplyLinkLabelsAsync();
        }, cancellationToken);

    public Task RenderHtmlAsync(string htmlBody, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            await _host.EvaluateAsync($"window.WinoEditor.setContent({Quote(Convert.ToBase64String(Encoding.UTF8.GetBytes(htmlBody)))}, 'replace')");
            IsContentLost = false;
        }, cancellationToken);

    public Task<string?> GetHtmlBodyAsync(CancellationToken cancellationToken = default) =>
        _host.Queue.RunAsync(async token =>
        {
            if (IsContentLost) throw new InvalidOperationException("The editor content was lost and has not been restored yet.");
            await EnsureReadyAsync(token);
            return await _host.EvaluateAsync("window.WinoEditor.getBodyContent()");
        }, cancellationToken);

    public Task SetThemeAsync(bool isDarkMode, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            _dark = isDarkMode;
            await _host.EvaluateAsync($"window.WinoEditor.setTheme({Bool(_dark)})");
        }, cancellationToken);

    public Task SetDefaultTypographyAsync(string? fontFamily, int fontSize, CancellationToken cancellationToken = default) =>
        RunAsync(() => _host.EvaluateAsync($"window.WinoEditor.setTypography({Quote(fontFamily)}, {Math.Clamp(fontSize, 8, 72)})"), cancellationToken);

    public Task FocusEditorAsync(bool focusControlAsWell, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            if (focusControlAsWell) await _host.OnUIAsync(() => Task.FromResult(View.GrabFocus()));
            await _host.EvaluateAsync("window.WinoEditor.focus()");
        }, cancellationToken);

    /// <summary>Freezes the document for quit; it stays frozen until restored or disposed.</summary>
    public Task SetClosePreparationAsync(bool prepared, CancellationToken cancellationToken = default) =>
        RunAsync(() => _host.EvaluateAsync(prepared
            ? "(()=>{document.activeElement?.blur();document.getElementById('wino-editor').contentEditable='false';return true;})()"
            : "(()=>{document.getElementById('wino-editor').contentEditable='true';return true;})()"), cancellationToken);

    public Task SetApplicationShortcutsAsync(IReadOnlyList<EditorApplicationShortcutGesture> shortcuts, CancellationToken cancellationToken = default) =>
        RunAsync(() => _host.EvaluateAsync($"window.WinoEditor.setApplicationShortcuts({JsonSerializer.Serialize(shortcuts, EditorJsonContext.Default.IReadOnlyListEditorApplicationShortcutGesture)})"), cancellationToken);

    public Task SetPasteAsHtmlAsync(bool pasteAsHtml, CancellationToken cancellationToken = default) =>
        RunAsync(() => _host.EvaluateAsync($"window.WinoEditor.setPasteAsHtml({Bool(pasteAsHtml)})"), cancellationToken);

    public Task InsertImagesAsync(IEnumerable<EditorImageInfo> images, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            foreach (var image in images)
            {
                if (!image.Data.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Inline images must be prepared image data URIs.", nameof(images));
                await _host.EvaluateAsync($"window.WinoEditor.insertImage({Quote(image.Data)})");
            }
        }, cancellationToken);

    /// <summary>
    /// Inserts the clipboard's image inline when it offers an image and no text; false leaves the paste to the page.
    /// WebKitGTK never exposes clipboard images to the page's paste event.
    /// </summary>
    public async Task<bool> PasteClipboardImageAsync(CancellationToken cancellationToken = default)
    {
        var dataUri = await _host.OnUIAsync(async () =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var clipboard = View.GetClipboard();
            if (!WebKitGtkClipboard.OffersImageOnly(clipboard)) return null;
            using var texture = await WebKitGtkClipboard.ReadTextureAsync(clipboard);
            return (string?)WebKitGtkClipboard.ToPngDataUri(texture);
        });
        if (dataUri is null) return false;
        await InsertImagesAsync([new EditorImageInfo(dataUri, "image.png")], cancellationToken);
        return true;
    }

    /// <summary>Runs a table command (addRow, removeRow, addColumn, removeColumn, deleteTable) on the table holding the caret.</summary>
    public Task<bool> TableCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (!TableCommands.Contains(command)) throw new ArgumentException($"'{command}' is not a table command.", nameof(command));
        return RunAsync(async () => await _host.EvaluateAsync($"window.WinoEditor.tableCommand({Quote(command)})") == "true", cancellationToken);
    }

    public Task<EditorFindResult> FindAsync(string query, bool ignoreCase, bool wholeWords, EditorFindDirection direction, CancellationToken cancellationToken = default) =>
        EvaluateFindAsync($"window.WinoEditor.find({Quote(query)}, {Bool(ignoreCase)}, {Bool(wholeWords)}, {Quote(direction.ToString().ToLowerInvariant())})", cancellationToken);

    public Task<EditorFindResult> ReplaceAsync(string replacement, CancellationToken cancellationToken = default) =>
        EvaluateFindAsync($"window.WinoEditor.replaceCurrent({Quote(replacement)})", cancellationToken);

    public Task<int> ReplaceAllAsync(string replacement, CancellationToken cancellationToken = default) =>
        RunAsync(async () => int.TryParse(await _host.EvaluateAsync($"window.WinoEditor.replaceAll({Quote(replacement)})"), out var count) ? count : 0, cancellationToken);

    public Task ClearFindAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _host.EvaluateAsync("window.WinoEditor.clearFind()"), cancellationToken);

    public Task SetSignatureAsync(string? html, string? previousHtml, CancellationToken cancellationToken = default) =>
        RunAsync(() => _host.EvaluateAsync($"window.WinoEditor.setSignature({Quote(Base64(html))}, {Quote(Base64(previousHtml))})"), cancellationToken);

    private Task<EditorFindResult> EvaluateFindAsync(string expression, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var parts = (await _host.EvaluateAsync(expression) ?? string.Empty).Split(',');
            return parts.Length == 2 && int.TryParse(parts[0], out var index) && int.TryParse(parts[1], out var count)
                ? new EditorFindResult(index, count)
                : new EditorFindResult(0, 0);
        }, cancellationToken);

    public Task ExecuteCommandAsync(EditorCommand command) => ExecuteCommandAsync(command, default);

    public Task ExecuteCommandAsync(EditorCommand command, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Kind == EditorCommandKind.InsertImage)
        {
            ImageInsertionRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        await _host.EvaluateAsync("window.WinoEditor.focus()");
        if (ExecName(command) is { } exec)
        {
            await _host.EvaluateAsync($"window.WinoEditor.exec({Quote(exec)}, {Quote(command.Value?.ToString())})");
            return;
        }
        if (command.Kind == EditorCommandKind.ToggleTheme) _dark = command.Value is true;
        await _host.EvaluateAsync(Expression(command, CurrentState));
    }, cancellationToken);

    private void MessageReceived(object? sender, EditorMessage message)
    {
        switch (message.Type)
        {
            case "contentChanged": ContentChanged?.Invoke(this, EventArgs.Empty); break;
            case "shortcut" when message.Command == "openLinkDialog": ShortcutRequested?.Invoke(this, EditorShortcutKind.OpenLinkDialog); break;
            case "applicationShortcut" when message.Gesture is not null: ApplicationShortcutRequested?.Invoke(this, message.Gesture); break;
            case "selectionState" when message.State is { } state:
                IsInTable = state.InTable;
                CurrentState = ToState(state);
                StateChanged?.Invoke(this, CurrentState);
                break;
        }
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync(() =>
    {
        _disposed = true;
        ContentChanged = null;
        StateChanged = null;
        ShortcutRequested = null;
        ApplicationShortcutRequested = null;
        ImageInsertionRequested = null;
        OperationFailed = null;
        Restarted = null;
    });
}
