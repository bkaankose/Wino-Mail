using Wino.Core.Domain.Interfaces;
using Wino.Editor;
using Wino.Mail.Rendering;
using Wino.Renderer;
using Wino.Renderer.Selection;
using EngineMailView = Wino.Renderer.UIKit.MailView;

namespace Wino.Mail.UIKit.Views.Mail.Compose;

/// <summary>Which match <see cref="UIKitEngineEditor.FindAsync"/> makes current.</summary>
internal enum EditorFindDirection
{
    /// <summary>A new or changed query: the first match, or the current one when only the text changed.</summary>
    Start,
    Next,
    Previous
}

/// <summary>One-based current match and total; (0, 0) when nothing matches.</summary>
internal readonly record struct EditorFindResult(int Index, int Count);

/// <summary>
/// The composer body on iPhone and iPad over the native renderer: the shared <see cref="EngineMailEditorSession"/> on
/// a UIKit engine view, with the members the compose page, the format toolbar and the rich text dialogs use (find and
/// replace, link opening, errors). The session is the <see cref="IHtmlMailEditorSession"/> and the
/// <see cref="Wino.Mail.ViewModels.IComposeBodyWriter"/>.
/// </summary>
internal sealed class UIKitEngineEditor : IAsyncDisposable
{
    private readonly EngineMailView _view;
    private IExternalLauncher? _launcher;

    public UIKitEngineEditor(EngineMailView view, IDispatcher dispatcher, MailEngineHost engineHost, IMailDialogService dialogService)
    {
        _view = view;
        Engine = new EngineMailEditorSession(view, dispatcher, engineHost, dialogService);
        Engine.LinkOpenRequested += (_, uri) => _ = OpenLinkAsync(uri);
        Engine.ContentChanged += (_, e) => ContentChanged?.Invoke(this, e);
        Engine.StateChanged += (_, state) => StateChanged?.Invoke(this, state);
        Engine.ShortcutRequested += (_, kind) => ShortcutRequested?.Invoke(this, kind);
        Engine.ApplicationShortcutRequested += (_, gesture) => ApplicationShortcutRequested?.Invoke(this, gesture);
        _view.FilesReceived += (_, files) => FilesReceived?.Invoke(this, files);
    }

    public EngineMailEditorSession Engine { get; }
    public IHtmlMailEditorSession Session => Engine;
    public EditorState CurrentState => Engine.CurrentState;

    /// <summary>The engine never loses its content to a crashed process; kept for the page's recovery path.</summary>
    public bool IsContentLost => false;

    public event EventHandler? ContentChanged;
    public event EventHandler<EditorState>? StateChanged;
    public event EventHandler<EditorShortcutKind>? ShortcutRequested;
    public event EventHandler<EditorApplicationShortcutGesture>? ApplicationShortcutRequested;
    public event EventHandler<IReadOnlyList<string>>? FilesReceived;
    public event EventHandler<Exception>? OperationFailed;

    /// <summary>The engine view picks and inserts images itself through the dialog service; never raised.</summary>
    public event EventHandler? ImageInsertionRequested
    {
        add { }
        remove { }
    }

    /// <summary>The web view could restart its content process; the engine cannot, so this is never raised.</summary>
    public event EventHandler? Restarted
    {
        add { }
        remove { }
    }

    public void Configure(IExternalLauncher launcher) => _launcher = launcher;

    public Task SetLinkLabelsAsync(string openLabel, string removeLabel, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Engine.InitializeAsync(cancellationToken);
    public Task RenderHtmlAsync(string htmlBody, CancellationToken cancellationToken = default) => Engine.RenderHtmlAsync(htmlBody, cancellationToken);
    public Task<string?> GetHtmlBodyAsync(CancellationToken cancellationToken = default) => Engine.GetHtmlBodyAsync(cancellationToken);
    public Task SetThemeAsync(bool isDarkMode, CancellationToken cancellationToken = default) => Engine.SetThemeAsync(isDarkMode, cancellationToken);
    public Task SetDefaultTypographyAsync(string? fontFamily, int fontSize, CancellationToken cancellationToken = default) => Engine.SetDefaultTypographyAsync(fontFamily, fontSize, cancellationToken);
    public Task SetSignatureAsync(string? html, string? previousHtml, CancellationToken cancellationToken = default) => Engine.SetSignatureAsync(html, previousHtml, cancellationToken);
    public Task FocusEditorAsync(bool focusControlAsWell, CancellationToken cancellationToken = default) => Engine.FocusEditorAsync(focusControlAsWell, cancellationToken);
    public Task InsertImagesAsync(IEnumerable<EditorImageInfo> images, CancellationToken cancellationToken = default) => Engine.InsertImagesAsync(images, cancellationToken);
    public Task ExecuteCommandAsync(EditorCommand command) => Engine.ExecuteCommandAsync(command);
    public Task SetRemoteContentPolicyAsync(RemoteContentPolicy policy, CancellationToken cancellationToken = default) => Engine.SetRemoteContentPolicyAsync(policy, cancellationToken);

    public Task<EditorFindResult> FindAsync(string query, bool ignoreCase, bool wholeWords, EditorFindDirection direction, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query))
        {
            _view.ClearFind();
            return Task.FromResult(default(EditorFindResult));
        }

        var result = _view.Find(new FindOptions(query, ignoreCase, wholeWords), direction switch
        {
            EditorFindDirection.Next => FindDirection.Next,
            EditorFindDirection.Previous => FindDirection.Previous,
            _ => FindDirection.Start,
        });
        return Task.FromResult(ToResult(result));
    }

    public Task<EditorFindResult> ReplaceAsync(string replacement, CancellationToken cancellationToken = default)
    {
        if (_view.Composer is not { } composer) return Task.FromResult(default(EditorFindResult));
        var result = composer.ReplaceCurrent(replacement);
        _view.Controller.Invalidate();
        return Task.FromResult(ToResult(result));
    }

    public Task<int> ReplaceAllAsync(string replacement, CancellationToken cancellationToken = default)
    {
        var count = _view.Composer?.ReplaceAll(replacement) ?? 0;
        _view.Controller.Invalidate();
        return Task.FromResult(count);
    }

    public Task ClearFindAsync(CancellationToken cancellationToken = default)
    {
        _view.ClearFind();
        return Task.CompletedTask;
    }

    private static EditorFindResult ToResult(FindResult result) => result.Count <= 0 ? default : new EditorFindResult(result.CurrentIndex + 1, result.Count);

    private async Task OpenLinkAsync(Uri uri)
    {
        try
        {
            if (_launcher is not null) await _launcher.LaunchUriAsync(uri);
        }
        catch (Exception exception)
        {
            OperationFailed?.Invoke(this, exception);
        }
    }

    public ValueTask DisposeAsync() => Engine.DisposeAsync();
}
