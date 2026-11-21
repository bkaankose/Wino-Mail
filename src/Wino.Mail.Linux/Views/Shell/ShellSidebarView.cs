using System.Collections.Specialized;
using System.ComponentModel;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.MenuItems.Contracts;
using Wino.Mail.ViewModels.Shell;
using Wino.Presentation.Linux;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux.Bindings;

namespace Wino.Mail.Linux.Views.Shell;

/// <summary>
/// The account and folder sidebar: the shared shell menu as flat rows (<see cref="ShellMenuRowPresentation"/>). Every
/// click goes to <see cref="WinoAppShellViewModel"/>. A row navigates on selection as well as on activation, so
/// accessibility clients that can only select drive it too.
/// </summary>
internal sealed class ShellSidebarView : IDisposable
{
    private readonly WinoAppShellViewModel _viewModel;
    private readonly BindingScope _bindings;
    private readonly Gtk.ListBox _menu = Gtk.ListBox.New();
    private readonly Adw.WindowTitle _title = Adw.WindowTitle.New("Wino Mail", string.Empty);
    private readonly Adw.Bin _footerHost = Adw.Bin.New();
    private readonly List<IMenuItem> _rows = [];
    private readonly List<Action> _subscriptions = [];
    private readonly MainLoop.Coalescer _rebuild;
    private int _lastInvoked = -1;
    private bool _applyingSelection;
    private bool _disposed;

    public ShellSidebarView(WinoAppShellViewModel viewModel, GtkBinder<WinoAppShellViewModel> bind)
    {
        _viewModel = viewModel;
        _rebuild = new MainLoop.Coalescer(Rebuild);
        var child = bind.Child();
        _bindings = child.Scope;

        _menu.AddCssClass("navigation-sidebar");
        _menu.SetSelectionMode(Gtk.SelectionMode.Single);
        var menu = _menu;
        _bindings.Own(GtkSignals.Connect<Gtk.ListBox, Gtk.ListBox.RowActivatedSignalArgs>(
            h => menu.OnRowActivated += h, h => menu.OnRowActivated -= h, (_, args) => Invoke(args.Row.GetIndex())));
        _bindings.Own(GtkSignals.Connect<Gtk.ListBox, Gtk.ListBox.RowSelectedSignalArgs>(
            h => menu.OnRowSelected += h, h => menu.OnRowSelected -= h, (_, args) => Selected(args.Row)));

        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.SetVexpand(true);
        scroll.SetChild(_menu);
        var header = Adw.HeaderBar.New();
        header.SetTitleWidget(_title);
        _footerHost.SetVisible(false);
        var root = Adw.ToolbarView.New();
        root.AddTopBar(header);
        root.SetContent(scroll);
        root.AddBottomBar(_footerHost);
        Root = root;

        child.Bind(nameof(WinoAppShellViewModel.CurrentMenu), static vm => vm.CurrentMenu, _ => _rebuild.Request());
        child.Bind(nameof(WinoAppShellViewModel.SelectedMenuItem), static vm => vm.SelectedMenuItem, _ => _rebuild.Request());
    }

    public Gtk.Widget Root { get; }
    public event Action? ItemInvoked;

    public void SetTitle(string title) => _title.SetTitle(title);

    public void SetFooter(Gtk.Widget? footer)
    {
        _footerHost.SetChild(footer);
        _footerHost.SetVisible(footer is not null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rebuild.Dispose();
        ClearSubscriptions();
        _bindings.Dispose();
        ItemInvoked = null;
    }

    private void Rebuild()
    {
        if (_disposed) return;
        ClearSubscriptions();
        _applyingSelection = true;
        try
        {
            _menu.RemoveAll();
            _rows.Clear();
            if (_viewModel.CurrentMenu is not { } menu) return;

            Watch(menu.Items);
            Gtk.ListBoxRow? selected = null;
            foreach (var (item, depth) in ShellMenuRowPresentation.Flatten(menu.Items, includeNewItems: false))
            {
                Watch(item);
                Watch(ShellMenuItemRules.Children(item));
                var row = ShellMenuRow.Create(item, ShellMenuRowPresentation.Describe(item, depth, null));
                _menu.Append(row);
                _rows.Add(item);
                if (ReferenceEquals(item, _viewModel.SelectedMenuItem)) selected = row;
            }

            if (selected is not null) _menu.SelectRow(selected);
            else _menu.UnselectAll();
        }
        finally { _applyingSelection = false; }
    }

    // Only a selection made from outside (an accessibility client) navigates. GTK also selects the row the keyboard focus
    // lands on, when the user moves it and when libadwaita moves it into a sidebar it docks; such a row navigates on
    // activation, and the list shows the current folder again unless that row was invoked. The selection can arrive
    // during a layout pass, so everything starts on the next main-loop turn.
    private void Selected(Gtk.ListBoxRow? row)
    {
        if (_applyingSelection || row is null) return;
        var index = row.GetIndex();
        var focused = row.HasFocus;
        MainLoop.Post(() =>
        {
            if (_disposed || !ReferenceEquals(_menu.GetSelectedRow(), row)) return;
            if (!focused) Invoke(index);
            else if (_lastInvoked != index) ShowCurrentSelection();
        });
    }

    private void ShowCurrentSelection()
    {
        var index = _viewModel.SelectedMenuItem is IMenuItem item ? _rows.IndexOf(item) : -1;
        _applyingSelection = true;
        try
        {
            if (index >= 0 && _menu.GetRowAtIndex(index) is { } row) _menu.SelectRow(row);
            else _menu.UnselectAll();
        }
        finally { _applyingSelection = false; }
    }

    private async void Invoke(int index)
    {
        if (_disposed || index < 0 || index >= _rows.Count) return;
        // A selection and an activation of the same row in one main-loop turn invoke it once.
        if (_lastInvoked == index) return;
        _lastInvoked = index;
        MainLoop.Post(() => _lastInvoked = -1);

        var item = _rows[index];
        var navigated = false;
        try
        {
            if (!ShellMenuItemRules.IsEnabled(item)) return;
            // Parents that only group their children (More) expand in place, as in the Android drawer. The rule is
            // repeated here because the proof of concept leaves shared code unchanged; it belongs in ShellMenuItemRules.
            if (ShellMenuRowPresentation.HasChildren(item) && item is not IAccountMenuItem && !ShellMenuItemRules.SelectsOnInvoked(item))
            {
                item.IsExpanded = !item.IsExpanded;
                return;
            }
            // Folders select themselves when their navigation starts (MailAppShellViewModel.NavigateFolderAsync).
            if (_viewModel.HandlesSelection && ShellMenuItemRules.SelectsOnInvoked(item) && item is not IBaseFolderMenuItem)
                _viewModel.SelectedMenuItem = item;
            navigated = true;
            await _viewModel.InvokeMenuItemAsync(item);
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "A shell menu item failed."); }
        finally
        {
            // The list selected the clicked row itself; show the ViewModel's selection again.
            _rebuild.Request();
            if (navigated && !_disposed) ItemInvoked?.Invoke();
        }
    }

    private void Watch(object? source)
    {
        if (source is INotifyCollectionChanged collection)
        {
            NotifyCollectionChangedEventHandler handler = (_, _) => _rebuild.Request();
            collection.CollectionChanged += handler;
            _subscriptions.Add(() => collection.CollectionChanged -= handler);
        }
        else if (source is INotifyPropertyChanged item)
        {
            PropertyChangedEventHandler handler = (_, _) => _rebuild.Request();
            item.PropertyChanged += handler;
            _subscriptions.Add(() => item.PropertyChanged -= handler);
        }
    }

    private void ClearSubscriptions()
    {
        foreach (var unsubscribe in _subscriptions) unsubscribe();
        _subscriptions.Clear();
    }
}
