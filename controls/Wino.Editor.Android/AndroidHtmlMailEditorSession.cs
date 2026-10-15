using System.Text;
using System.Text.Json;
using Android.Content;
using Android.Views;
using AndroidX.Core.View;
using Wino.Core.Domain.Interfaces;
using static Wino.Editor.Android.AndroidHtmlMailReaderSession;
using WebView = Android.Webkit.WebView;

namespace Wino.Editor.Android;

/// <summary>
/// The message editor over an Android <see cref="WebView"/> (the twin of WebKitHtmlMailEditorSession): the shared
/// contenteditable editor document (Wino.Editor.Core <c>editor.html</c>/<c>editor.js</c>) in one generation served by
/// <see cref="AndroidWebViewSession"/>, with every operation serialized through <see cref="SessionOperationQueue"/>.
/// Formatting commands map to <c>window.WinoEditor</c> exactly as on the Mac and iOS; selection state, content changes
/// and the link shortcut come back over the <c>winoMail</c> bridge. Insert picture raises
/// <see cref="ImageInsertionRequested"/> so the head can open the photo picker; pasted or keyboard-inserted images
/// arrive through <see cref="AcceptReceivedImages"/> (OnReceiveContentListener) and are inserted inline as data URIs.
/// </summary>
public sealed class AndroidHtmlMailEditorSession : IHtmlMailEditorSession
{
    /// <summary>Inline images larger than this are not inserted (they would bloat the draft MIME).</summary>
    public const long MaximumInlineImageBytes = 10 * 1024 * 1024;

    private readonly AndroidWebViewSession _host;
    private bool _dark;
    private bool _receivesImages;

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

    /// <summary>Insert picture: the head picks the images and calls <see cref="InsertImagesAsync"/>.</summary>
    public event EventHandler? ImageInsertionRequested;

    public event EventHandler<Exception>? OperationFailed;

    public AndroidHtmlMailEditorSession(WebView browser)
    {
        _host = new(browser);
        _host.Message += MessageReceived;
        _host.OperationFailed += (_, e) => OperationFailed?.Invoke(this, e);
    }

    public WebView Browser => _host.Browser;

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
        _host.Queue.RunAsync(async () =>
        {
            _dark = isDarkMode;
            if (_host.IsReady) await _host.EvaluateAsync($"window.WinoEditor.setTheme({Bool(_dark)})");
        }, cancellationToken);

    public Task SetDefaultTypographyAsync(string? fontFamily, int fontSize, CancellationToken cancellationToken = default) =>
        RunAsync(() => _host.EvaluateAsync($"window.WinoEditor.setTypography({Quote(fontFamily)}, {Math.Clamp(fontSize, 8, 72)})"), cancellationToken);

    /// <summary>Focuses the editor; with <paramref name="focusControlAsWell"/> the WebView takes focus and the keyboard opens.</summary>
    public Task FocusEditorAsync(bool focusControlAsWell, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            if (focusControlAsWell) await _host.OnUIAsync(() =>
            {
                _host.Browser.RequestFocus(FocusSearchDirection.Down);
                return Task.CompletedTask;
            });
            await _host.EvaluateAsync("window.WinoEditor.focus()");
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

    /// <summary>
    /// Swaps the signature block (<c>div[data-wino-signature]</c>); empty <paramref name="html"/> removes it.
    /// <paramref name="previousHtml"/> locates a signature inserted before the wrapper existed.
    /// </summary>
    public Task SetSignatureAsync(string? html, string? previousHtml, CancellationToken cancellationToken = default) =>
        RunAsync(() => _host.EvaluateAsync($"window.WinoEditor.setSignature({Quote(Base64(html))}, {Quote(Base64(previousHtml))})"), cancellationToken);

    private static string Base64(string? value) => string.IsNullOrEmpty(value) ? string.Empty : Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Inserts images the WebView receives from paste, the keyboard (stickers, GIF keyboards) or drag and drop inline:
    /// an OnReceiveContentListener for <c>image/*</c> reads each item through <paramref name="resolver"/> and inserts
    /// it as a data URI; text and anything that cannot be read go back to the WebView. Main thread; once per session.
    /// </summary>
    public void AcceptReceivedImages(ContentResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (_receivesImages) return;
        _receivesImages = true;
        ViewCompat.SetOnReceiveContentListener(_host.Browser, ["image/*"], new ImageReceiver(this, resolver));
    }

    /// <summary>Reads an image from a content URI as an inline data URI; null when it is not an image or too large.</summary>
    public static EditorImageInfo? ReadImage(ContentResolver resolver, global::Android.Net.Uri uri, string? fallbackName = null)
    {
        var type = resolver.GetType(uri);
        if (type is null || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
        using var input = resolver.OpenInputStream(uri);
        if (input is null) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaximumInlineImageBytes) return null;
        }
        return new EditorImageInfo($"data:{type};base64,{Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length)}",
            fallbackName ?? uri.LastPathSegment ?? "image");
    }

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
        // Preference commands must not move the caret or open the keyboard.
        if (command.Kind is not (EditorCommandKind.ToggleSpellCheck or EditorCommandKind.SetSpellCheckLanguage or EditorCommandKind.ToggleAutoCorrect or EditorCommandKind.ToggleTheme))
            await _host.EvaluateAsync("window.WinoEditor.focus()");
        if (exec is not null)
        {
            // Chromium moves the caret to the start of the block when it builds or changes a list or indentation;
            // keep it at the same place in the text (WebKit keeps it on its own).
            bool keepCaret = command.Kind is EditorCommandKind.ToggleOrderedList or EditorCommandKind.ToggleUnorderedList
                or EditorCommandKind.Indent or EditorCommandKind.Outdent;
            if (keepCaret) await _host.EvaluateAsync(SaveCaretScript);
            await _host.EvaluateAsync($"window.WinoEditor.exec({Quote(exec)}, {Quote(command.Value?.ToString())})");
            if (keepCaret) await _host.EvaluateAsync(RestoreCaretScript);
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
            _ => throw new NotSupportedException($"The Android editor cannot execute {command.Kind}.")
        };
        if (command.Kind == EditorCommandKind.InsertImage) ImageInsertionRequested?.Invoke(this, EventArgs.Empty);
        else await _host.EvaluateAsync(expression);
    }, cancellationToken);

    /// <summary>Remembers the selection as text offsets in the editor (structure changes keep the text).</summary>
    private const string SaveCaretScript = """
        (() => {
            const editor = document.getElementById('wino-editor');
            const selection = window.getSelection();
            window.__winoCaret = null;
            if (!editor || !selection || !selection.rangeCount) return false;
            const range = selection.getRangeAt(0);
            if (!editor.contains(range.startContainer)) return false;
            const before = document.createRange();
            before.selectNodeContents(editor);
            before.setEnd(range.startContainer, range.startOffset);
            const start = before.toString().length;
            window.__winoCaret = { start, end: start + range.toString().length };
            return true;
        })()
        """;

    /// <summary>Puts the selection back at the remembered text offsets.</summary>
    private const string RestoreCaretScript = """
        (() => {
            const saved = window.__winoCaret;
            window.__winoCaret = null;
            const editor = document.getElementById('wino-editor');
            if (!saved || !editor) return false;
            const walker = document.createTreeWalker(editor, NodeFilter.SHOW_TEXT);
            let node, seen = 0, startNode = null, startOffset = 0, endNode = null, endOffset = 0;
            while ((node = walker.nextNode())) {
                const length = node.textContent.length;
                if (!startNode && saved.start <= seen + length) { startNode = node; startOffset = saved.start - seen; }
                if (!endNode && saved.end <= seen + length) { endNode = node; endOffset = saved.end - seen; break; }
                seen += length;
            }
            if (!startNode || !endNode) return false;
            const range = document.createRange();
            range.setStart(startNode, startOffset);
            range.setEnd(endNode, endOffset);
            const selection = window.getSelection();
            selection.removeAllRanges();
            selection.addRange(range);
            window.WinoEditor.rememberSelection();
            return true;
        })()
        """;

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
        if (_receivesImages) ViewCompat.SetOnReceiveContentListener(_host.Browser, null, null);
        ContentChanged = null;
        StateChanged = null;
        ShortcutRequested = null;
        ApplicationShortcutRequested = null;
        ImageInsertionRequested = null;
        OperationFailed = null;
    });

    /// <summary>Takes the image items of received content; the rest (text) is handled by the WebView.</summary>
    private sealed class ImageReceiver(AndroidHtmlMailEditorSession owner, ContentResolver resolver) : Java.Lang.Object, AndroidX.Core.View.IOnReceiveContentListener
    {
        public ContentInfoCompat? OnReceiveContent(View view, ContentInfoCompat payload)
        {
            var split = payload.Partition(new ImagePredicate());
            if (split?.First is not ContentInfoCompat images) return payload;
            var clip = images.Clip;
            var picked = new List<EditorImageInfo>();
            for (int index = 0; index < clip.ItemCount; index++)
            {
                if (clip.GetItemAt(index)?.Uri is not { } uri) continue;
                try
                {
                    if (ReadImage(resolver, uri) is { } image) picked.Add(image);
                }
                catch (Exception exception) when (exception is Java.Lang.Exception or IOException)
                {
                    owner.OperationFailed?.Invoke(owner, exception);
                }
            }
            if (picked.Count > 0) _ = InsertAsync(picked);
            return split.Second as ContentInfoCompat;
        }

        private async Task InsertAsync(IReadOnlyList<EditorImageInfo> images)
        {
            try { await owner.InsertImagesAsync(images); }
            catch (ObjectDisposedException) { }
            catch (Exception exception) { owner.OperationFailed?.Invoke(owner, exception); }
        }
    }

    private sealed class ImagePredicate : Java.Lang.Object, AndroidX.Core.Util.IPredicate
    {
        public bool Test(Java.Lang.Object? item) => item is ClipData.Item { Uri: not null };
    }
}
