using Wino.Mail.Linux.Infrastructure;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views;

/// <summary>
/// A window besides the main window (reader, composer, event, What's New, onboarding): an <c>Adw.Window</c> with a header
/// bar, any content, Ctrl+W, an optional shortcut router and a size remembered per <see cref="Kind"/>. Every close (the
/// user's or <see cref="Close"/>) hides the window, awaits <see cref="Closing"/>, then destroys it. Main thread only.
/// </summary>
internal sealed class SecondaryWindow
{
    private readonly WindowStateStore? _sizes;
    private readonly Adw.ToolbarView _toolbar;
    private readonly Adw.WindowTitle _title;
    private readonly Gtk.ShortcutController _keys;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CloseTarget _closeTarget;
    private IDisposable? _router;
    private Gtk.Widget _content;
    private bool _closing;

    public SecondaryWindow(Gtk.Application application, string kind, Gtk.Widget content, string title, int defaultWidth, int defaultHeight,
        WindowStateStore? sizes = null, int minimumWidth = 360, int minimumHeight = 240)
    {
        Kind = kind;
        _sizes = sizes;
        _content = content;

        Window = Adw.Window.New();
        Window.SetApplication(application);
        var placement = sizes?.ReadKind(kind);
        Window.SetDefaultSize(Math.Max(placement?.Width ?? defaultWidth, minimumWidth), Math.Max(placement?.Height ?? defaultHeight, minimumHeight));
        if (placement is { IsMaximized: true }) Window.Maximize();
        Window.SetSizeRequest(minimumWidth, minimumHeight);
        Window.SetTitle(title);

        _title = Adw.WindowTitle.New(title, string.Empty);
        HeaderBar = Adw.HeaderBar.New();
        HeaderBar.SetTitleWidget(_title);
        _toolbar = Adw.ToolbarView.New();
        _toolbar.AddTopBar(HeaderBar);
        _toolbar.SetContent(content);
        Window.SetContent(_toolbar);

        _keys = Gtk.ShortcutController.New();
        _keys.AddShortcut(Gtk.Shortcut.New(Gtk.ShortcutTrigger.ParseString("<Control>w"), Gtk.NamedAction.New("window.close")));
        Window.AddController(_keys);

        // The native close-request handler stays in Gir.Core's table after -=; it reaches this window through a box
        // that the close empties, so a closed window is not rooted by its own signal.
        _closeTarget = new CloseTarget(this);
        var target = _closeTarget;
        Window.OnCloseRequest += (_, _) => target.Window?.OnCloseRequest() ?? false;
        WinoLiveObjects.Track(this);
    }

    public Adw.Window Window { get; }
    public string Kind { get; }
    public Adw.HeaderBar HeaderBar { get; }

    public string Title
    {
        get => _title.GetTitle() ?? string.Empty;
        set
        {
            _title.SetTitle(value);
            Window.SetTitle(value);
        }
    }

    public string? Subtitle
    {
        get => _title.GetSubtitle() is { Length: > 0 } subtitle ? subtitle : null;
        set => _title.SetSubtitle(value ?? string.Empty);
    }

    /// <summary>Replacing the content unparents the old widget; its owner releases it.</summary>
    public Gtk.Widget Content
    {
        get => _content;
        set
        {
            _content = value;
            if (!_closing) _toolbar.SetContent(value);
        }
    }

    /// <summary>Runs once, before the window is destroyed: release the view here.</summary>
    public Func<Task>? Closing { get; set; }

    /// <summary>Completes after <see cref="Closing"/> ran and the window was destroyed.</summary>
    public Task Closed => _closed.Task;

    public bool ClosedByUser { get; private set; }
    public bool IsClosing => _closing;

    /// <summary>Attaches the window's shortcut router (SH-06 <c>ShortcutRouter</c> with <c>IsPopOut</c>); it is disposed on close.</summary>
    public void AttachShortcuts(Func<Gtk.Window, IDisposable?> createRouter)
    {
        if (_closing) return;
        _router?.Dispose();
        _router = createRouter(Window);
    }

    /// <summary>Keeps <paramref name="subscription"/> until the window closes.</summary>
    public void Own(IDisposable subscription)
    {
        if (_closing) subscription.Dispose();
        else _subscriptions.Add(subscription);
    }

    public void Present()
    {
        if (!_closing) Window.Present();
    }

    public void Close()
    {
        if (_closing) return;
        _ = CloseAsync(byUser: false);
    }

    private bool OnCloseRequest()
    {
        if (!_closing) _ = CloseAsync(byUser: true);
        return true;
    }

    private async Task CloseAsync(bool byUser)
    {
        _closing = true;
        ClosedByUser = byUser;
        try
        {
            if (_sizes is not null)
            {
                Window.GetDefaultSize(out var width, out var height);
                _sizes.WriteKind(Kind, new WindowPlacement(width, height, Window.IsMaximized()));
            }
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "The size of the {Kind} window could not be saved.", Kind); }

        Window.SetVisible(false);
        try
        {
            if (Closing is { } closing) await closing();
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Closing the {Kind} window failed.", Kind); }
        finally
        {
            Closing = null;
            _router?.Dispose();
            _router = null;
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
            // Unparenting the content first closes any popover inside it before the surface goes away.
            _toolbar.SetContent(null);
            Window.RemoveController(_keys);
            _closeTarget.Window = null;
            Window.Destroy();
            Serilog.Log.Debug("The {Kind} window closed ({Origin}).", Kind, byUser ? "user" : "app");
            _closed.TrySetResult();
        }
    }

    private sealed class CloseTarget(SecondaryWindow window)
    {
        public SecondaryWindow? Window = window;
    }
}
