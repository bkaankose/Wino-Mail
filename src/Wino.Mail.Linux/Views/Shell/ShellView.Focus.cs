using System.Diagnostics;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Infrastructure;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Shell;

internal enum ShellPane { None, Sidebar, Content, Detail, Trailing }

/// <summary>
/// Keyboard movement between the shell's panes: F6 and Shift+F6 cycle the visible panes, Escape steps back to the
/// content, Enter in a list moves into the detail, and a window whose focus fell out of the tree gets it back in the
/// content. Panes say where focus lands through <see cref="IPaneFocus"/> and <see cref="IDetailPane"/>.
/// </summary>
internal sealed partial class ShellView
{
    public const string KeyboardClass = "wino-keyboard";
    private const long PendingDetailFocusMilliseconds = 2000;

    private MainLoop.Coalescer? _focusFallback;
    private long _pendingDetailFocusAt;
    private ShellPane _lastPane = ShellPane.Content;

    private Gtk.Window? Window => _navigation.MainWindow?.Window;

    internal ShellPane CurrentPane() => PaneOf(Window?.GetFocus());

    internal ShellPane PaneOf(Gtk.Widget? focus)
    {
        for (var widget = focus; widget is not null; widget = widget.GetParent())
        {
            if (AppActions.IsSame(widget, _sidebar.Root)) return ShellPane.Sidebar;
            if (AppActions.IsSame(widget, _pane.Root)) return ShellPane.Detail;
            if (AppActions.IsSame(widget, _layout.TrailingHost)) return ShellPane.Trailing;
            if (_content is IDetailPane { DetailRoot: { } detail } && AppActions.IsSame(widget, detail)) return ShellPane.Detail;
            if (_content is { } content && AppActions.IsSame(widget, content.Root)) return ShellPane.Content;
            if (AppActions.IsSame(widget, _header.Root)) return ShellPane.Content;
        }
        return ShellPane.None;
    }

    /// <summary>The panes F6 visits in order; the stacked layout offers only the page it shows.</summary>
    internal List<ShellPane> AvailablePanes()
    {
        var panes = new List<ShellPane>(4);
        if (_layout.IsSidebarVisible) panes.Add(ShellPane.Sidebar);
        var stacked = _layout.IsStacked;
        if (_content is not null && !(stacked && _layout.ShowsDetail)) panes.Add(ShellPane.Content);
        if (HasDetail() && !(stacked && !_layout.ShowsDetail)) panes.Add(ShellPane.Detail);
        if (_layout.IsTrailingPanelShown) panes.Add(ShellPane.Trailing);
        return panes;
    }

    internal bool FocusPane(ShellPane pane)
    {
        if (IsReleased) return false;
        _pendingDetailFocusAt = 0;
        var focused = pane switch
        {
            ShellPane.Sidebar => FocusSidebar(),
            ShellPane.Content => FocusContent(),
            ShellPane.Detail => FocusDetail(),
            ShellPane.Trailing => _layout.TrailingHost.ChildFocus(Gtk.DirectionType.TabForward),
            _ => false
        };
        if (focused) ShowKeyboardFocus();
        return focused;
    }

    internal bool CycleFocus(int delta)
    {
        var panes = AvailablePanes();
        if (panes.Count == 0) return false;
        var index = panes.IndexOf(CurrentPane());
        var start = index >= 0 ? index : delta > 0 ? -1 : panes.Count;
        for (var step = 1; step <= panes.Count; step++)
        {
            var next = ((start + delta * step) % panes.Count + panes.Count) % panes.Count;
            if (FocusPane(panes[next])) return true;
        }
        return false;
    }

    /// <summary>Enter on a list row: the detail now, and again once a reading-pane page that is still loading has shown.</summary>
    internal void RequestDetailFocus()
    {
        if (IsReleased) return;
        FocusPane(ShellPane.Detail);
        _pendingDetailFocusAt = Stopwatch.GetTimestamp();
    }

    /// <summary>Escape: from the detail, the trailing panel or an overlay sidebar back to the content. False passes the key on (the list clears its selection).</summary>
    internal bool PaneBack()
    {
        switch (CurrentPane())
        {
            case ShellPane.Detail:
                if (_content is IDetailPane detail)
                {
                    detail.CloseDetail();
                    FocusPane(ShellPane.Content);
                    return true;
                }
                if (_layout.IsStacked && _layout.ShowsDetail)
                {
                    GoBack();
                    MainLoop.Post(() => FocusPane(ShellPane.Content));
                    return true;
                }
                return FocusPane(ShellPane.Content);
            case ShellPane.Trailing:
                return FocusPane(ShellPane.Content);
            case ShellPane.Sidebar when _layout.IsSidebarOverlay && _layout.IsSidebarVisible:
                _layout.DismissOverlaySidebar();
                return FocusPane(ShellPane.Content);
            case ShellPane.None:
                return FocusPane(ShellPane.Content);
            default:
                return false;
        }
    }

    /// <summary>Single keys act in Mail with the focus in the list or the reader, never in the sidebar (type-ahead) or the header.</summary>
    internal bool AllowsSingleKeys(Gtk.Widget? focus)
        => !IsReleased && ActiveMode == WinoApplicationMode.Mail && focus is not null && !Contains(_header.Root, focus)
           && PaneOf(focus) is ShellPane.Content or ShellPane.Detail;

    /// <summary>The fixed keys the shell runs (<see cref="LinuxKeymap"/>); false when the key does not apply now.</summary>
    internal bool TryHandleKeyAction(string action)
    {
        if (IsReleased) return false;
        switch (action)
        {
            case LinuxKeymap.NextPane: return CycleFocus(1);
            case LinuxKeymap.PreviousPane: return CycleFocus(-1);
            case LinuxKeymap.PaneBack: return PaneBack();
            case LinuxKeymap.NextItem: return _content is IItemNavigation next && next.MoveItem(1);
            case LinuxKeymap.PreviousItem: return _content is IItemNavigation previous && previous.MoveItem(-1);
            case LinuxKeymap.FocusSearch:
                _header.FocusSearch();
                return true;
        }
        if (_content is IKeyActionTarget page && page.TryHandleKeyAction(action)) return true;
        return _content is IUsesReadingPane && !_pane.IsCovered && _pane.CurrentView is IKeyActionTarget pane && pane.TryHandleKeyAction(action);
    }

#if DEBUG
    internal ShellHeaderBar DebugHeader => _header;

    internal string DescribeFocus()
    {
        var focus = Window?.GetFocus();
        return $"pane={CurrentPane()} panes={string.Join(',', AvailablePanes())} focus={focus?.GetType().FullName ?? "-"} stacked={_layout.IsStacked} detailShown={_layout.ShowsDetail} " +
               $"keyboardClass={_layout.Root.HasCssClass(KeyboardClass)} focusVisible={Window?.GetFocusVisible()} pane={_pane.CurrentView?.GetType().Name ?? "-"} sidebarRow={_sidebar.DebugFocusedTitle ?? "-"}";
    }
#endif

    internal static bool Contains(Gtk.Widget ancestor, Gtk.Widget? widget)
    {
        for (var current = widget; current is not null; current = current.GetParent())
        {
            if (AppActions.IsSame(current, ancestor)) return true;
        }
        return false;
    }

    private bool HasDetail() => _content switch
    {
        IUsesReadingPane => !_pane.IsCovered && _pane.CurrentView is IPaneFocus,
        IDetailPane detail => detail.HasDetail,
        _ => false
    };

    private bool FocusSidebar()
    {
        if (!_layout.IsSidebarVisible) _layout.ToggleSidebar();
        return _sidebar.FocusList();
    }

    private bool FocusContent()
    {
        if (IsReleased || _content is not { } content || (_layout.IsStacked && _layout.ShowsDetail)) return false;
        if (content is IPaneFocus primary && primary.FocusPrimary()) return true;
        return content.Root.ChildFocus(Gtk.DirectionType.TabForward);
    }

    private bool FocusDetail()
    {
        if (_content is IDetailPane detail) return detail.HasDetail && detail.FocusDetail();
        if (_content is not IUsesReadingPane || _pane.IsCovered || (_layout.IsStacked && !_layout.ShowsDetail)) return false;
        return _pane.CurrentView is IPaneFocus view && view.FocusPrimary();
    }

    /// <summary>A reading-pane page finished showing: a recent Enter moves the focus into it.</summary>
    private void DetailShown(WinoPage page)
    {
        if (IsReleased || page == WinoPage.IdlePage || _pendingDetailFocusAt == 0) return;
        var requested = _pendingDetailFocusAt;
        _pendingDetailFocusAt = 0;
        if (Stopwatch.GetElapsedTime(requested).TotalMilliseconds > PendingDetailFocusMilliseconds) return;
        if (FocusDetail()) ShowKeyboardFocus();
    }

    /// <summary>After a page change the content takes the focus, unless the user is in the sidebar or the header.</summary>
    private void FocusNewContent()
    {
        if (IsReleased || Window is not { } window) return;
        var focus = window.GetFocus();
        // The window's first focus lands on the sidebar's scrolled window; only a row the user moved to keeps it.
        if (focus is not null && (Contains(_sidebar.List, focus) || Contains(_header.Root, focus))) return;
        FocusContent();
    }

    private void WireKeyboardFocus()
    {
        if (Window is not { } window) return;
        _focusFallback = new MainLoop.Coalescer(RestoreLostFocus);
        Bindings.Own(_focusFallback);
        Bindings.Own(GtkSignals.OnNotify(window, "focus-widget", () =>
        {
            if (IsReleased || Window is not { } current) return;
            var focus = current.GetFocus();
            if (IsLost(focus))
            {
                if (current.IsActive) _focusFallback?.Request();
            }
            else if (PaneOf(focus) is not ShellPane.None and var pane) _lastPane = pane;
        }));
        Bindings.Own(GtkSignals.OnNotify(window, "focus-visible", () =>
        {
            if (Window?.GetFocusVisible() == true) ShowKeyboardFocus();
        }));
        // GTK drops focus-visible on its own terms; the pane line stays until the pointer is used.
        var click = Gtk.GestureClick.New();
        click.SetButton(0);
        click.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        Bindings.Own(GtkSignals.Connect<Gtk.GestureClick, Gtk.GestureClick.PressedSignalArgs>(
            h => click.OnPressed += h, h => click.OnPressed -= h, (_, _) => _layout.Root.RemoveCssClass(KeyboardClass)));
        _layout.Root.AddController(click);
        Bindings.Own(new ActionDisposable(() => _layout.Root.RemoveController(click)));
    }

    // Removing or hiding the focused widget (a closed composer, the reader while it loads the next message) leaves the
    // window without focus, or GTK parks it on the list and reader split, so the next key went nowhere. The pane that had
    // it takes it back, else the content, unless a dialog is up.
    private void RestoreLostFocus()
    {
        if (IsReleased || Window is not Adw.ApplicationWindow window || !IsLost(window.GetFocus()) || !window.IsActive) return;
        if (window.GetVisibleDialog() is not null || _navigation.MainWindow?.IsLocked == true) return;
        var restored = _lastPane switch
        {
            ShellPane.Detail => FocusDetail(),
            ShellPane.Sidebar => _sidebar.FocusList(),
            _ => false
        };
        // The stacked layout hides the list while it shows the reader.
        if (!restored && !FocusContent()) FocusDetail();
        Serilog.Log.Debug("Keyboard focus was lost; restored to {Pane}.", CurrentPane());
    }

    private static bool IsLost(Gtk.Widget? focus) => focus is null or Gtk.Paned;

    private void ShowKeyboardFocus()
    {
        if (IsReleased) return;
        Window?.SetFocusVisible(true);
        _layout.Root.AddCssClass(KeyboardClass);
    }
}
