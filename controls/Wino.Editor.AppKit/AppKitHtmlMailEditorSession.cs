using Foundation;
using System.Text;
using System.Text.Json;
using AppKit;
using WebKit;

using Wino.Core.Domain.Interfaces;
using Wino.Editor;
using static Wino.Editor.AppKit.AppKitHtmlMailReaderSession;

namespace Wino.Editor.AppKit;

public sealed class AppKitHtmlMailEditorSession : NSObject, IHtmlMailEditorSession
{
    private readonly AppKitBrowserSession _host;
    private bool _dark;
    public IHtmlMailEditorSession Session => this;
    public EditorState CurrentState { get; private set; } = new();
    public EditorCommandCapabilities Capabilities { get; } = new()
    {
        Fonts = new[] { "Arial", "Helvetica", "Times New Roman", "Courier New" },
        FontSizes = new[] { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 48, 72 },
        Alignments = new[] { EditorTextAlignment.Left, EditorTextAlignment.Center, EditorTextAlignment.Right, EditorTextAlignment.Justify },
        ParagraphStyles = new[] { new EditorParagraphStyleOption("Paragraph", "p"), new EditorParagraphStyleOption("Heading 1", "h1"), new EditorParagraphStyleOption("Heading 2", "h2"), new EditorParagraphStyleOption("Heading 3", "h3"), new EditorParagraphStyleOption("Quote", "blockquote"), new EditorParagraphStyleOption("Preformatted", "pre"), new EditorParagraphStyleOption("Code", "code") },
        LineHeights = new[] { "normal", "1", "1.15", "1.5", "2" }
    };
    public event EventHandler? ContentChanged;
    public event EventHandler<EditorState>? StateChanged;
    public event EventHandler<EditorShortcutKind>? ShortcutRequested;
    public event EventHandler<EditorApplicationShortcutGesture>? ApplicationShortcutRequested;
    public event EventHandler? ImageInsertionRequested;
    public event EventHandler<Exception>? OperationFailed;

    public AppKitHtmlMailEditorSession(WKWebView browser)
    {
        _host = new(browser);
        _host.Message += MessageReceived;
        _host.OperationFailed += (_, e) => OperationFailed?.Invoke(this, e);
    }

    public void Configure(IExternalLauncher launcher) => _host.Configure(launcher);
    private async Task EnsureReadyAsync(CancellationToken token)
    {
        if (!_host.IsReady) await _host.LoadAsync(true, RemoteContentPolicy.Blocked, _dark, token);
    }

    private Task RunAsync(Func<Task> operation, CancellationToken token) => _host.Queue.RunAsync(async lifetime =>
    {
        await EnsureReadyAsync(lifetime);
        await operation();
    }, token);

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _host.Queue.RunAsync(EnsureReadyAsync, cancellationToken);
    public Task RenderHtmlAsync(string htmlBody, CancellationToken cancellationToken = default) =>
        RunAsync(() => _host.EvaluateAsync($"window.WinoEditor.setContent({Quote(Convert.ToBase64String(Encoding.UTF8.GetBytes(htmlBody)))}, 'replace')"), cancellationToken);
    public Task<string?> GetHtmlBodyAsync(CancellationToken cancellationToken = default) =>
        _host.Queue.RunAsync(async token => { await EnsureReadyAsync(token); return await _host.EvaluateAsync("window.WinoEditor.getBodyContent()"); }, cancellationToken);
    public Task SetThemeAsync(bool isDarkMode, CancellationToken cancellationToken = default) =>
        RunAsync(async () => { _dark = isDarkMode; await _host.EvaluateAsync($"window.WinoEditor.setTheme({Bool(_dark)})"); }, cancellationToken);
    public Task SetDefaultTypographyAsync(string? fontFamily, int fontSize, CancellationToken cancellationToken = default) =>
        RunAsync(() => _host.EvaluateAsync($"window.WinoEditor.setTypography({Quote(fontFamily)}, {Math.Clamp(fontSize, 8, 72)})"), cancellationToken);
    public Task FocusEditorAsync(bool focusControlAsWell, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            if (focusControlAsWell) await _host.OnUIAsync(() => { _host.Browser.Window?.MakeFirstResponder(_host.Browser); return Task.CompletedTask; });
            await _host.EvaluateAsync("window.WinoEditor.focus()");
        }, cancellationToken);
    /// <summary>Head-owned quit preparation; remains frozen until explicitly restored or disposed.</summary>
    public Task SetClosePreparationAsync(bool prepared, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            await _host.EvaluateAsync(prepared
                ? "(()=>{document.activeElement?.blur();document.getElementById('wino-editor').contentEditable='false';return true;})()"
                : "(()=>{document.getElementById('wino-editor').contentEditable='true';return true;})()");
        }, cancellationToken);
    public Task SetApplicationShortcutsAsync(IReadOnlyList<EditorApplicationShortcutGesture> shortcuts, CancellationToken cancellationToken = default) =>
        RunAsync(() => _host.EvaluateAsync($"window.WinoEditor.setApplicationShortcuts({JsonSerializer.Serialize(shortcuts, EditorJsonContext.Default.IReadOnlyListEditorApplicationShortcutGesture)})"), cancellationToken);
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

    public Task ExecuteCommandAsync(EditorCommand command) => ExecuteCommandAsync(command, default);
    public Task ExecuteCommandAsync(EditorCommand command, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        string? exec = command.Kind switch
        {
            EditorCommandKind.ToggleBold => "bold", EditorCommandKind.ToggleItalic => "italic",
            EditorCommandKind.ToggleUnderline => "underline", EditorCommandKind.ToggleStrikethrough => "strikeThrough",
            EditorCommandKind.ToggleOrderedList => "insertOrderedList", EditorCommandKind.ToggleUnorderedList => "insertUnorderedList",
            EditorCommandKind.Indent => "indent", EditorCommandKind.Outdent => "outdent",
            EditorCommandKind.Undo => "undo", EditorCommandKind.Redo => "redo",
            EditorCommandKind.ClearFormatting => "removeFormat", EditorCommandKind.SetFontFamily => "fontName",
            EditorCommandKind.SetFontSize => "fontSize", EditorCommandKind.SetTextColor => "foreColor",
            EditorCommandKind.SetHighlightColor => "backColor",
            EditorCommandKind.SetAlignment => command.Value is EditorTextAlignment a ? a switch
            { EditorTextAlignment.Center => "justifyCenter", EditorTextAlignment.Right => "justifyRight", EditorTextAlignment.Justify => "justifyFull", _ => "justifyLeft" } : throw new ArgumentException("Alignment is required."),
            _ => null
        };
        await _host.EvaluateAsync("window.WinoEditor.focus()");
        if (exec is not null)
        {
            await _host.EvaluateAsync($"window.WinoEditor.exec({Quote(exec)}, {Quote(command.Value?.ToString())})");
            return;
        }
        if (command.Kind == EditorCommandKind.ToggleTheme) _dark = command.Value is true;
        string expression = command.Kind switch
        {
            EditorCommandKind.SetParagraphStyle => $"window.WinoEditor.setParagraphStyle({Quote(command.Value?.ToString() ?? "p")})",
            EditorCommandKind.SetLineHeight => $"window.WinoEditor.setLineHeight({Quote(command.Value?.ToString() ?? "normal")})",
            EditorCommandKind.SetImageProperties when command.Value is EditorImagePropertiesCommandArgs image => $"window.WinoEditorImages.setSelectedProperties({JsonSerializer.Serialize(image, EditorJsonContext.Default.EditorImagePropertiesCommandArgs)})",
            EditorCommandKind.InsertLink when command.Value is EditorLinkCommandArgs link => CurrentState.IsImageSelected
                ? $"window.WinoEditorImages.setSelectedProperties({JsonSerializer.Serialize(new EditorImagePropertiesCommandArgs(CurrentState.ImageAltText ?? string.Empty, link.Url, link.OpenInNewWindow), EditorJsonContext.Default.EditorImagePropertiesCommandArgs)})"
                : $"window.WinoEditor.createLink({Quote(link.Url)}, {Quote(link.Text)}, {Bool(link.OpenInNewWindow)})",
            EditorCommandKind.RemoveLink => CurrentState.IsImageSelected
                ? $"window.WinoEditorImages.setSelectedProperties({JsonSerializer.Serialize(new EditorImagePropertiesCommandArgs(CurrentState.ImageAltText ?? string.Empty), EditorJsonContext.Default.EditorImagePropertiesCommandArgs)})"
                : "window.WinoEditor.removeLink()",
            EditorCommandKind.InsertEmoji => $"window.WinoEditor.insertEmoji({Quote(command.Value?.ToString() ?? "😊")})",
            EditorCommandKind.InsertTable when command.Value is EditorTableCommandArgs table => $"window.WinoEditor.insertTable({Math.Clamp(table.Rows, 1, 20)}, {Math.Clamp(table.Columns, 1, 20)})",
            EditorCommandKind.ToggleTheme => $"window.WinoEditor.setTheme({Bool(command.Value is true)})",
            EditorCommandKind.ToggleSpellCheck => $"window.WinoEditor.setSpellCheck({Bool(command.Value is true)})",
            EditorCommandKind.SetSpellCheckLanguage => $"window.WinoEditor.setSpellCheckLanguage({Quote(command.Value?.ToString())})",
            EditorCommandKind.ToggleAutoCorrect => $"window.WinoEditor.setAutoCorrect({Bool(command.Value is true)})",
            EditorCommandKind.InsertImage => string.Empty,
            _ => throw new NotSupportedException($"The macOS editor cannot execute {command.Kind}.")
        };
        if (command.Kind == EditorCommandKind.InsertImage) ImageInsertionRequested?.Invoke(this, EventArgs.Empty);
        else await _host.EvaluateAsync(expression);
    }, cancellationToken);

    private void MessageReceived(object? sender, EditorMessage message)
    {
        switch (message.Type)
        {
            case "contentChanged": ContentChanged?.Invoke(this, EventArgs.Empty); break;
            case "shortcut" when message.Command == "openLinkDialog": ShortcutRequested?.Invoke(this, EditorShortcutKind.OpenLinkDialog); break;
            case "applicationShortcut" when message.Gesture is not null: ApplicationShortcutRequested?.Invoke(this, message.Gesture); break;
            case "selectionState" when message.State is { } s:
                CurrentState = new()
                {
                    IsBold = s.Bold, IsItalic = s.Italic, IsUnderline = s.Underline, IsStrikethrough = s.Strikethrough,
                    IsOrderedList = s.OrderedList, IsUnorderedList = s.UnorderedList, HasSelection = s.HasSelection,
                    IsImageSelected = s.ImageSelected, IsDarkMode = s.DarkMode, IsSpellCheckEnabled = s.SpellCheck,
                    Alignment = Enum.TryParse<EditorTextAlignment>(s.Alignment, true, out var a) ? a : EditorTextAlignment.Left,
                    FontFamily = s.FontFamily, FontSize = s.FontSize, ParagraphStyle = s.ParagraphStyle,
                    TextColor = s.Color, HighlightColor = s.HighlightColor, LineHeight = s.LineHeight,
                    LinkUrl = s.LinkUrl, ImageAltText = s.ImageAltText, ImageLinkUrl = s.ImageLinkUrl, SelectedText = s.SelectedText
                };
                StateChanged?.Invoke(this, CurrentState);
                break;
        }
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync(() =>
    {
        ContentChanged = null;
        StateChanged = null;
        ShortcutRequested = null;
        ApplicationShortcutRequested = null;
        ImageInsertionRequested = null;
        OperationFailed = null;

    });
}
