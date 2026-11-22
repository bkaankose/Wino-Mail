using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.MenuItems.Contracts;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Shell;

/// <summary>
/// Shows the sidebar's context menus (<see cref="ShellSidebarContextMenus"/>) on right click, long press, the Menu key
/// and Shift+F10. Each cell gets one <see cref="ContextMenu"/> attachment; the keys are handled on the list because the
/// focus sits on the list item around the cell, never inside it.
/// </summary>
internal sealed class ShellSidebarMenuPresenter : IDisposable
{
    private readonly ShellSidebarView _sidebar;
    private readonly ShellSidebarMenuContext _context;
    private readonly Dictionary<ShellSidebarCell, ContextMenuAttachment> _attachments = new(ReferenceEqualityComparer.Instance);
    private readonly Action<ShellSidebarCell, Gtk.ListItem>? _previousSetup;
    private readonly Action<ShellSidebarCell>? _previousTeardown;
    private readonly Gtk.EventControllerKey _keys = Gtk.EventControllerKey.New();
    private readonly IDisposable _keyPressed;
    private readonly Action _rowsChanged;
    private ContextMenuAttachment? _open;
    private bool _disposed;

    public ShellSidebarMenuPresenter(ShellSidebarView sidebar, ShellSidebarMenuContext context)
    {
        _sidebar = sidebar;
        _context = context;
        WinoLiveObjects.Track(this);
        _previousSetup = sidebar.CellSetup;
        _previousTeardown = sidebar.CellTeardown;
        sidebar.CellSetup = (cell, listItem) =>
        {
            _previousSetup?.Invoke(cell, listItem);
            Attach(cell);
        };
        sidebar.CellTeardown = cell =>
        {
            _previousTeardown?.Invoke(cell);
            Detach(cell);
        };
        _keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        var keys = _keys;
        _keyPressed = GtkSignals.Connect<Gtk.EventControllerKey, Gtk.EventControllerKey.KeyPressedSignalArgs, bool>(
            h => keys.OnKeyPressed += h, h => keys.OnKeyPressed -= h, (_, args) => OnKeyPressed(args));
        sidebar.List.AddController(_keys);
        _rowsChanged = Close;
        sidebar.RowsChanged += _rowsChanged;
    }

    public bool IsOpen => _open?.Current is not null;

    /// <summary>Opens the row's menu at (<paramref name="x"/>, <paramref name="y"/>) in the cell. False when the row has none.</summary>
    public bool Show(IMenuItem item, double x, double y)
    {
        if (_disposed || _sidebar.CellOf(item) is not { } cell || !_attachments.TryGetValue(cell, out var attachment)) return false;
        Close();
        if (attachment.Open(x, y) is null) return false;
        _open = attachment;
        return true;
    }

    public void Close()
    {
        _open?.Current?.Popdown();
        _open = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
        _sidebar.RowsChanged -= _rowsChanged;
        _keyPressed.Dispose();
        _sidebar.List.RemoveController(_keys);
        foreach (var attachment in _attachments.Values) attachment.Dispose();
        _attachments.Clear();
        _sidebar.CellSetup = _previousSetup;
        _sidebar.CellTeardown = _previousTeardown;
    }

    private void Attach(ShellSidebarCell cell)
    {
        if (_disposed || _attachments.ContainsKey(cell)) return;
        _attachments[cell] = ContextMenu.Attach(cell.Root, builder =>
        {
            if (cell.Item is { } item && ShellMenuItemRules.IsEnabled(item)) Populate(builder, Entries(item));
        });
    }

    private void Detach(ShellSidebarCell cell)
    {
        if (!_attachments.Remove(cell, out var attachment)) return;
        if (ReferenceEquals(_open, attachment)) Close();
        attachment.Dispose();
    }

    private SidebarMenuEntry[] Entries(IMenuItem item)
    {
        try { return ShellSidebarContextMenus.Build(item, _context); }
        catch (Exception exception)
        {
            _context.Error(exception);
            return [];
        }
    }

    private void Populate(WinoMenuBuilder builder, SidebarMenuEntry[] entries)
    {
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                builder.Separator();
                continue;
            }
            if (entry.Children is { Length: > 0 } children)
            {
                builder.Submenu(entry.Title, sub => Populate(sub, children), entry.IsEnabled, entry.Glyph);
                continue;
            }
            if (entry.Run is not { } run) continue;
            builder.Item(entry.Title, () => Observe(run), entry.IsEnabled, icon: entry.Glyph, destructive: entry.IsDestructive);
        }
    }

    private async void Observe(Func<Task> run)
    {
        try { await run(); }
        catch (Exception exception) { _context.Error(exception); }
    }

    private bool OnKeyPressed(Gtk.EventControllerKey.KeyPressedSignalArgs args)
    {
        var menuKey = args.Keyval == Gdk.Constants.KEY_Menu
            || (args.Keyval == Gdk.Constants.KEY_F10 && (args.State & Gdk.ModifierType.ShiftMask) != 0);
        if (!menuKey || _sidebar.FocusedItem() is not { } item || _sidebar.CellOf(item) is not { } cell) return false;
        return Show(item, 24, cell.Root.GetHeight() / 2.0);
    }
}
