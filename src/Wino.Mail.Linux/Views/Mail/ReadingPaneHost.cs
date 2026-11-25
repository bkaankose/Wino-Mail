using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Infrastructure;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The reading pane. The reader (an <see cref="IRenavigableView"/>) and the idle view are created once and kept, so one
/// web view and one ViewModel serve every message; any other page is created per request and released when another
/// page becomes visible. Every member runs on the main thread.
/// </summary>
internal sealed class ReadingPaneHost : IRenderingFrameHost, IRenderingFrameAdopter
{
    private readonly Action<Exception> _error;
    private readonly Gtk.Overlay _root = Gtk.Overlay.New();
    private readonly Gtk.Stack _stack = Gtk.Stack.New();
    private readonly Adw.Bin _cover = Adw.Bin.New();
    private readonly Gtk.Widget _empty;
    private readonly Dictionary<WinoPage, IWinoView> _retained = [];
    private IWinoView? _transient;
    private IWinoView? _visible;
    private int _version;
    private bool _released;

    public ReadingPaneHost(Action<Exception> error)
    {
        _error = error;
        _stack.SetTransitionType(Gtk.StackTransitionType.None);
        _empty = Adw.Bin.New();
        _stack.AddNamed(_empty, "empty");
        _cover.SetVisible(false);
        _root.SetChild(_stack);
        _root.AddOverlay(_cover);
    }

    public Gtk.Widget Root => _root;

    public object? CurrentViewModel => _visible?.AssociatedViewModel;

    public IWinoView? CurrentView => _visible;

    /// <summary>True while a list page covers the pane (the multi-selection summary).</summary>
    public bool IsCovered => _cover.GetVisible();

    public async Task ShowAsync(WinoPage page, object? parameter, Func<IWinoView> create)
    {
        if (_released) return;
        var version = ++_version;
        var created = false;
        if (!_retained.TryGetValue(page, out var view))
        {
            view = create();
            created = true;
            if (IsRetained(view))
            {
                _retained[page] = view;
                _stack.AddNamed(view.Root, page.ToString());
            }
            else _stack.AddChild(view.Root);
        }

        var replaced = ReferenceEquals(_transient, view) ? null : _transient;
        _transient = IsRetained(view) ? null : view;
        _stack.SetVisibleChild(view.Root);
        _visible = view;

        try
        {
            if (created) await view.ActivateAsync(NavigationMode.New, parameter);
            else if (view is IRenavigableView renavigable) await renavigable.RenavigateAsync(parameter);
        }
        catch (Exception exception)
        {
            _error(exception);
            await DropAsync(page, view);
            if (version == _version) ShowIdle();
        }

        // The replaced view is no longer visible, so it is released even when a newer request owns the pane now.
        if (replaced is not null) await DropAsync(null, replaced);
    }

    public async Task ClearAsync()
    {
        if (_released) return;
        var version = ++_version;
        var replaced = _transient;
        _transient = null;
        ShowIdle();
        if (replaced is not null) await DropAsync(null, replaced);
        // The retained idle page keeps the state it was last shown with (no mail account, an account to fix); a cleared
        // pane is the plain idle page, as navigating to it without a parameter on the other platforms.
        if (version == _version && _visible is IRenavigableView idle && !_released) await idle.RenavigateAsync(null);
    }

    /// <summary>Hiding the stack unmaps the shown page (the web view), so a covered reader costs nothing.</summary>
    public void SetCover(Gtk.Widget? cover)
    {
        if (_released && cover is not null) return;
        _cover.SetChild(cover);
        _cover.SetVisible(cover is not null);
        _stack.SetVisible(cover is null);
    }

    public bool Detach(IWinoView view)
    {
        if (_released) return false;
        WinoPage? page = null;
        foreach (var entry in _retained)
        {
            if (ReferenceEquals(entry.Value, view)) page = entry.Key;
        }
        if (page is { } key) _retained.Remove(key);
        else if (ReferenceEquals(_transient, view)) _transient = null;
        else return false;
        ++_version;
        var wasVisible = ReferenceEquals(_visible, view);
        if (wasVisible) _visible = null;
        if (view.Root.GetParent() is not null) _stack.Remove(view.Root);
        if (wasVisible) ShowIdle();
        return true;
    }

    public bool Adopt(WinoPage page, IWinoView view)
    {
        if (_released) return false;
        ++_version;
        var replaced = _transient is { } transient && !ReferenceEquals(transient, view) ? transient : null;
        if (view.Root.GetParent() is null) _stack.AddChild(view.Root);
        _transient = view;
        _visible = view;
        _stack.SetVisibleChild(view.Root);
        if (replaced is not null) _ = DropAsync(null, replaced);
        return true;
    }

    public async Task ReleaseAsync()
    {
        if (_released) return;
        SetCover(null);
        _released = true;
        ++_version;
        var views = new List<IWinoView>(_retained.Values);
        if (_transient is not null) views.Add(_transient);
        _retained.Clear();
        _transient = null;
        _visible = null;
        foreach (var view in views) _stack.Remove(view.Root);
        _stack.Remove(_empty);
        foreach (var view in views)
        {
            try { await view.ReleaseAsync(); }
            catch (Exception exception) { _error(exception); }
        }
    }

    private static bool IsRetained(IWinoView view) => view is IRenavigableView or IdleView;

    private void ShowIdle()
    {
        if (_released) return;
        _visible = _retained.TryGetValue(WinoPage.IdlePage, out var idle) ? idle : null;
        _stack.SetVisibleChild(_visible?.Root ?? _empty);
    }

    private async Task DropAsync(WinoPage? page, IWinoView view)
    {
        if (page is { } key && _retained.TryGetValue(key, out var kept) && ReferenceEquals(kept, view)) _retained.Remove(key);
        if (ReferenceEquals(_transient, view)) _transient = null;
        if (ReferenceEquals(_visible, view)) _visible = null;
        if (view.Root.GetParent() is not null) _stack.Remove(view.Root);
        try { await view.ReleaseAsync(); }
        catch (Exception exception) { _error(exception); }
    }
}
