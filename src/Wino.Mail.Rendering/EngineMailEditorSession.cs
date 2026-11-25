using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Editor;
using Wino.Mail.ViewModels;
using Wino.Renderer;
using Wino.Renderer.Compose;
using Wino.Renderer.Editing;
using Wino.Renderer.Mime;
using Wino.Renderer.Platform;
using Wino.Renderer.Policy;
using Wino.Renderer.Primitives;
using Wino.Renderer.Theme;

namespace Wino.Mail.Rendering;

/// <summary>
/// The editor contract over an <see cref="IMailView"/> in composer mode. Every load opens a new composer session in the
/// view, so the session keeps the settings (theme, typography, spelling, shortcuts) and applies them to each one.
/// Members may be called from any thread; they run on the view's UI thread through the head's dispatcher.
/// </summary>
public sealed partial class EngineMailEditorSession : IHtmlMailEditorSession, IComposeBodyWriter
{
    private const string DefaultFontFamily = "Calibri";
    private static readonly ShortcutGesture LinkDialogGesture = new(Key.K, Control: true, Alt: false, Shift: false, Command: false);

    private readonly IMailView _view;
    private readonly IDispatcher _dispatcher;
    private readonly MailEngineHost _engineHost;
    private readonly IMailDialogService _dialogService;
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _attachCancellation = new();
    private readonly Dictionary<ShortcutGesture, EditorApplicationShortcutGesture> _applicationShortcuts = [];
    private MailComposerSession? _subscribedComposer;
    private Task _loadTask = Task.CompletedTask;
    private int _loadGeneration;
    private bool _isContentCurrent;
    private EditorCommandCapabilities? _capabilities;
    private ComposeTypography _typography = new(DefaultFontFamily, 14);

    // The family the user last picked in this document. The default typography only applies until then: an empty
    // line, such as the one Enter opens, keeps the picked family instead of falling back to the inherited default.
    private string? _pickedFontFamily;
    private bool _isPickedFontQueued;
    private string? _spellCheckLanguage;
    private bool _isDarkMode;
    private bool _isHighContrast;
    private bool _isSpellCheckEnabled = true;
    private bool _isAutoCorrectEnabled;
    private bool _blockRemoteContent;
    private bool _disposed;

    public EngineMailEditorSession(IMailView view, IDispatcher dispatcher, MailEngineHost engineHost, IMailDialogService dialogService)
    {
        _view = view;
        _dispatcher = dispatcher;
        _engineHost = engineHost;
        _dialogService = dialogService;

        _view.EditStateChanged += View_EditStateChanged;
        _view.LinkActivated += View_LinkActivated;
        _ = WaitAttachedAsync();
    }

    private async Task WaitAttachedAsync()
    {
        try
        {
            await _view.WhenAttachedAsync(_attachCancellation.Token);
            _loaded.TrySetResult();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public EditorState CurrentState { get; private set; } = new();

    public EditorCommandCapabilities Capabilities => _capabilities ??= EngineEditorMapping.CreateCapabilities(TryGetBackend());

    public event EventHandler<EditorState>? StateChanged;
    public event EventHandler<EditorShortcutKind>? ShortcutRequested;
    public event EventHandler? ContentChanged;
    public event EventHandler<EditorApplicationShortcutGesture>? ApplicationShortcutRequested;

    /// <summary>Raised for Ctrl+click on a link and for "open link"; the host launches it.</summary>
    public event EventHandler<Uri>? LinkOpenRequested;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => RunAsync(() => WaitLoadedAsync(cancellationToken));

    public Task RenderHtmlAsync(string htmlBody, CancellationToken cancellationToken = default)
        => RunAsync(() => LoadAsync(
            options => string.IsNullOrWhiteSpace(htmlBody)
                ? LoadNewDocumentAsync(options, cancellationToken)
                : _view.LoadDraftAsync(HtmlMailContent.FromHtml(htmlBody), options, cancellationToken),
            cancellationToken));

    /// <summary>
    /// Opens the draft from its MIME: the engine picks the body and serves the draft's <c>cid:</c> images itself.
    /// </summary>
    public Task RenderDraftAsync(ComposeDraftRenderRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            _blockRemoteContent = request.ShouldBlockRemoteContent;

            return LoadAsync(
                options => _view.LoadDraftAsync(MimeMailContent.Create(request.Draft), options, cancellationToken),
                cancellationToken);
        });

    /// <summary>
    /// The body as one self-contained HTML string: inline images come back as <c>data:</c> URIs. A document without
    /// text, image, table or rule is the empty string, so an untouched editor never saves its empty paragraph markup
    /// as a signature, a template or calendar notes.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing was loaded, so there is no content to hand out.</exception>
    public Task<string?> GetHtmlBodyAsync(CancellationToken cancellationToken = default)
        => RunAsync<string?>(async () =>
        {
            var composer = await GetLoadedComposerAsync(cancellationToken);
            return composer.IsEmpty ? string.Empty : EngineEditorHtml.ToSelfContainedBody(composer.GetContent());
        });

    /// <exception cref="InvalidOperationException">Nothing was loaded; writing would replace the draft's body with an empty one.</exception>
    public Task ApplyAsync(BodyBuilder builder, CancellationToken cancellationToken)
        => RunAsync(async () =>
        {
            var composer = await GetLoadedComposerAsync(cancellationToken);
            OutgoingMimeBuilder.Apply(builder, EngineEditorHtml.ToLinkedResources(composer.GetContent()));
        });

    public Task SetThemeAsync(bool isDarkMode, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ApplyTheme(isDarkMode);
            return Task.CompletedTask;
        });

    public Task SetDefaultTypographyAsync(string? fontFamily, int fontSize, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _typography = new ComposeTypography(
                string.IsNullOrWhiteSpace(fontFamily) ? DefaultFontFamily : fontFamily,
                Math.Clamp(fontSize, 8, 72));
            _pickedFontFamily = null;
            _view.Composer?.Typography = _typography;
            RaiseStateChanged();

            return Task.CompletedTask;
        });

    public Task SetSignatureAsync(string? html, string? previousHtml, CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            if (await WhenIdleAsync(cancellationToken) is not { } composer) return;

            composer.SetSignature(string.IsNullOrWhiteSpace(html) ? null : new SignatureContent(html), previousHtml);
        });

    public Task FocusEditorAsync(bool focusControlAsWell, CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            await WhenIdleAsync(cancellationToken);
            _view.RequestFocus();
        });

    /// <summary>
    /// Only Control gestures on keys the editor does not use itself are registered, so a host shortcut can never take
    /// over typing or a standard editing command.
    /// </summary>
    public Task SetApplicationShortcutsAsync(IReadOnlyList<EditorApplicationShortcutGesture> shortcuts, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _applicationShortcuts.Clear();
            foreach (var shortcut in shortcuts)
            {
                var key = EngineEditorMapping.ToKey(shortcut.Key);
                if (!shortcut.Control || key == Key.Unknown || EngineEditorMapping.IsEditingKey(key)) continue;

                _applicationShortcuts[new ShortcutGesture(key, true, shortcut.Alt, shortcut.Shift, false)] = shortcut;
            }

            _view.Composer?.SetApplicationShortcuts(CreateEngineShortcuts());

            return Task.CompletedTask;
        });

    public Task InsertImagesAsync(IEnumerable<EditorImageInfo> images, CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            if (await WhenIdleAsync(cancellationToken) is null) return;

            foreach (var image in images)
            {
                var args = EngineEditorImages.Create(image, _engineHost.Backend.Images);
                if (_disposed || !_view.IsEditing) return;

                if (args is not null)
                    _view.InsertImages(new[] { args });
            }
        });

    public Task SetRemoteContentPolicyAsync(RemoteContentPolicy policy, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var block = policy == RemoteContentPolicy.Blocked;
            if (block == _blockRemoteContent) return Task.CompletedTask;

            _blockRemoteContent = block;

            // The composer that is open was created under the previous decision.
            if (_view.Composer is { } composer && _view.Engine is { } engine)
            {
                composer.SetUserAdjustments(new UserPolicyAdjustments
                {
                    RemoteContent = block ? RemoteContentMode.BlockAll : engine.Options.Policy.Resources.Mode
                });
            }

            return Task.CompletedTask;
        });

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _dispatcher.ExecuteOnUIThread(DisposeCore);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // The UI thread is gone, and the view with it.
            _disposed = true;
        }
    }

    private void DisposeCore()
    {
        if (_disposed) return;

        _disposed = true;

        if (_loaded.TrySetException(new ObjectDisposedException(nameof(EngineMailEditorSession))))
            _ = _loaded.Task.Exception;

        _attachCancellation.Cancel();
        _view.EditStateChanged -= View_EditStateChanged;
        _view.LinkActivated -= View_LinkActivated;
        SubscribeComposer(null);

        // Releases the composer session; the engine itself belongs to MailEngineHost.
        _view.Engine = null;

        StateChanged = null;
        ShortcutRequested = null;
        ContentChanged = null;
        ApplicationShortcutRequested = null;
        LinkOpenRequested = null;
    }

    private Task LoadAsync(Func<ComposerOptions, Task<ComposerLoadResult>> start, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Until this load shows its content the view still holds the previous document, which is no longer what the
        // host asked for. A read in that state throws instead of handing one document's content out as another's.
        _isContentCurrent = false;
        _pickedFontFamily = null;

        var load = LoadCoreAsync(++_loadGeneration, start, cancellationToken);
        _loadTask = load;
        return load;
    }

    private async Task LoadCoreAsync(int generation, Func<ComposerOptions, Task<ComposerLoadResult>> start, CancellationToken cancellationToken)
    {
        await WaitLoadedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        _view.Engine ??= _engineHost.Engine;

        var result = await start(CreateComposerOptions());
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The engine opens a body without any element as a document with no line for the caret, which cannot be
        // typed into. Such a draft starts as a new message instead.
        if (!result.IsCancelled && HasNoLine(_view.Composer))
        {
            result = await LoadNewDocumentAsync(CreateComposerOptions(), cancellationToken);
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        // A newer load took this one's place; that load shows its own content.
        if (result.IsCancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        _isContentCurrent = generation == _loadGeneration;

        SubscribeComposer(_view.Composer);
        ApplySettings();
        RaiseStateChanged();
    }

    private Task<ComposerLoadResult> LoadNewDocumentAsync(ComposerOptions options, CancellationToken cancellationToken)
        => _view.LoadComposeAsync(ComposeDocumentBuilder.CreateNew(_typography, null, null), null, options, cancellationToken);

    private static bool HasNoLine(MailComposerSession? composer)
    {
        if (composer is not { IsEmpty: true, Editor: { } editor }) return false;

        for (var child = editor.ComposeRoot.FirstChild; child is not null; child = child.NextSibling)
        {
            if (child is Wino.Renderer.Dom.Element) return false;
        }

        return true;
    }

    private Task WaitLoadedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _loaded.Task.IsCompleted ? _loaded.Task : _loaded.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// The composer once no load is running, so a read or an edit that follows a load always meets that load's
    /// content. Null when nothing is loaded.
    /// </summary>
    private async Task<MailComposerSession?> WhenIdleAsync(CancellationToken cancellationToken)
    {
        await WaitLoadedAsync(cancellationToken);

        while (_loadTask is { IsCompleted: false } load)
        {
            try
            {
                await load.WaitAsync(cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The load reports its failure to its own caller.
            }
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        return _view.IsEditing ? _view.Composer : null;
    }

    private async Task<MailComposerSession> GetLoadedComposerAsync(CancellationToken cancellationToken)
    {
        var composer = await WhenIdleAsync(cancellationToken);

        return composer is not null && _isContentCurrent
            ? composer
            : throw new InvalidOperationException("The editor has not loaded the document it was last given.");
    }

    private ComposerOptions CreateComposerOptions()
    {
        var policy = _view.Engine?.Options.Policy;

        return new ComposerOptions
        {
            Theme = CreateTheme(),
            Typography = _typography,
            SpellCheck = _isSpellCheckEnabled,
            AutoCorrect = _isSpellCheckEnabled && _isAutoCorrectEnabled,
            SpellCheckLanguage = _spellCheckLanguage,
            PasteAsHtml = _pasteAsHtml,
            Policy = _blockRemoteContent && policy is not null
                ? policy with { Resources = policy.Resources with { Mode = RemoteContentMode.BlockAll } }
                : null
        };
    }

    private ThemeOptions CreateTheme() => new()
    {
        IsDark = _isDarkMode,
        RootIsSurface = true,
        ForcedColors = _isHighContrast
    };

    private void ApplySettings()
    {
        if (_view.Composer is not { } composer) return;

        composer.Theme = CreateTheme();
        composer.Typography = _typography;
        ApplySpelling();
        composer.SetApplicationShortcuts(CreateEngineShortcuts());
    }

    private void ApplyTheme(bool isDarkMode)
    {
        if (_isDarkMode == isDarkMode) return;

        _isDarkMode = isDarkMode;
        _view.Composer?.Theme = CreateTheme();
        RaiseStateChanged();
    }

    /// <summary>
    /// The system's high contrast state for the window that shows the editor. Called on the UI thread by the head's
    /// editor element.
    /// </summary>
    public void SetHighContrast(bool isHighContrast)
    {
        if (_disposed || _isHighContrast == isHighContrast) return;

        _isHighContrast = isHighContrast;
        _view.Composer?.Theme = CreateTheme();
    }

    private void ApplySpelling()
    {
        if (_view.Composer is not { } composer) return;

        composer.SpellCheckLanguage = _spellCheckLanguage;
        composer.SpellCheck = _isSpellCheckEnabled;
        composer.AutoCorrect = _isSpellCheckEnabled && _isAutoCorrectEnabled;
    }

    private ShortcutGesture[] CreateEngineShortcuts() => [.. _applicationShortcuts.Keys, LinkDialogGesture];

    private void SubscribeComposer(MailComposerSession? composer)
    {
        if (ReferenceEquals(_subscribedComposer, composer)) return;

        if (_subscribedComposer is { } previous)
        {
            previous.ContentChanged -= Composer_ContentChanged;
            previous.ApplicationShortcutRequested -= Composer_ApplicationShortcutRequested;
        }

        _subscribedComposer = composer;

        if (composer is not null)
        {
            composer.ContentChanged += Composer_ContentChanged;
            composer.ApplicationShortcutRequested += Composer_ApplicationShortcutRequested;
        }
    }

    private void RaiseStateChanged()
    {
        if (_disposed) return;

        var editState = _view.EditState;
        var state = EngineEditorMapping.ToEditorState(
            editState,
            _typography.FontFamily,
            _typography.FontSizePx,
            _isDarkMode,
            _isSpellCheckEnabled);

        if (NeedsPickedFont(editState, state.FontFamily) is { } picked)
        {
            state = state with { FontFamily = picked };
            QueuePickedFont();
        }

        CurrentState = state;
        StateChanged?.Invoke(this, CurrentState);
    }

    private string? NeedsPickedFont(EditState? editState, string? reportedFontFamily)
        => _pickedFontFamily is { } picked
           && editState is { HasSelection: false, IsCaretOnEmptyLine: true }
           && !string.Equals(reportedFontFamily, picked, StringComparison.OrdinalIgnoreCase)
            ? picked
            : null;

    // Applied after the engine finishes raising its state change rather than from inside it: the yield posts the rest
    // to the UI thread's next turn (IDispatcher would run it inline). On a caret the engine only sets the style for
    // the next typed text, so the document and the undo history are unchanged.
    private async void QueuePickedFont()
    {
        if (_isPickedFontQueued) return;

        _isPickedFontQueued = true;
        await Task.Yield();
        _isPickedFontQueued = false;
        if (_disposed) return;

        try
        {
            var editState = _view.EditState;
            var reported = EngineEditorMapping.FirstFontFamily(editState?.FontFamily) ?? _typography.FontFamily;
            if (NeedsPickedFont(editState, reported) is { } picked)
                _view.Execute(EditCommandKind.SetFontFamily, new FontFamilyArgs(picked));
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not keep the picked font on an empty line.");
        }
    }

    private IPlatformBackend? TryGetBackend()
    {
        try
        {
            return _engineHost.Backend;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "The mail engine is unavailable; the editor offers the fallback font list.");
            return null;
        }
    }

    private void View_EditStateChanged(object? sender, EventArgs e) => RaiseStateChanged();

    private void View_LinkActivated(object? sender, LinkEventArgs e)
    {
        var address = string.IsNullOrWhiteSpace(e.Link.Original) ? e.Link.Effective : e.Link.Original;

        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp ||
             uri.Scheme == Uri.UriSchemeHttps ||
             uri.Scheme == Uri.UriSchemeMailto ||
             uri.Scheme == Uri.UriSchemeFtp))
        {
            LinkOpenRequested?.Invoke(this, uri);
        }
    }

    private void Composer_ContentChanged(object? sender, EventArgs e) => ContentChanged?.Invoke(this, EventArgs.Empty);

    private void Composer_ApplicationShortcutRequested(object? sender, ShortcutGesture gesture)
    {
        if (_applicationShortcuts.TryGetValue(gesture, out var shortcut))
            ApplicationShortcutRequested?.Invoke(this, shortcut);
        else if (gesture == LinkDialogGesture)
            ShortcutRequested?.Invoke(this, EditorShortcutKind.OpenLinkDialog);
    }

    private Task RunAsync(Func<Task> operation)
        => RunAsync(async () =>
        {
            await operation();
            return true;
        });

    // The head's dispatcher runs the action at once when already on the UI thread, else queues it.
    private Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task scheduled;
        try
        {
            scheduled = _dispatcher.ExecuteOnUIThread(() => _ = CompleteAsync(operation, completion));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
            return completion.Task;
        }

        scheduled.ContinueWith(
            static (task, state) => ((TaskCompletionSource<T>)state!).TrySetException(
                new ObjectDisposedException(nameof(EngineMailEditorSession), task.Exception)),
            completion,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return completion.Task;
    }

    private static async Task CompleteAsync<T>(Func<Task<T>> operation, TaskCompletionSource<T> completion)
    {
        try
        {
            completion.TrySetResult(await operation());
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }
}
