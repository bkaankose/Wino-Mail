using System.Collections.Specialized;
using System.ComponentModel;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Views.Mail;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.MenuItems.Contracts;
using Wino.Mail.ViewModels.Shell;

namespace Wino.Mail.Linux.Views.Shell;

/// <summary>
/// The shell: the account and folder sidebar beside the mail list and the reader. It shows the shared shell menu as
/// flat rows (<see cref="ShellMenuRowPresentation"/>) and hands every click to <see cref="WinoAppShellViewModel"/>.
/// Mail is the only mode of the proof of concept.
/// </summary>
internal sealed class ShellView : WinoView<WinoAppShellViewModel>
{
    private readonly GtkNavigationService _navigation;
    private readonly ShellModeActivator _modes;
    private readonly ReadingPaneHost _pane;
    private readonly Gtk.ListBox _menu = Gtk.ListBox.New();
    private readonly Adw.Bin _listHost = Adw.Bin.New();
    private readonly List<IMenuItem> _rows = [];
    private readonly List<Action> _subscriptions = [];
    private IWinoView? _content;
    private int _contentVersion;
    private bool _rebuildQueued;

    public ShellView(WinoAppShellViewModel viewModel, WinoViewServices services, GtkNavigationService navigation) : base(viewModel, services)
    {
        _navigation = navigation;
        _modes = new ShellModeActivator(ViewModel, ViewModel.StatePersistenceService, exception => Serilog.Log.Error(exception, "Shell mode activation failed."));
        _pane = new ReadingPaneHost(ReportError);

        _menu.AddCssClass("navigation-sidebar");
        _menu.SetSelectionMode(Gtk.SelectionMode.Single);
        _menu.OnRowActivated += (_, args) => Invoke(args.Row.GetIndex());

        var menuScroll = Gtk.ScrolledWindow.New();
        menuScroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        menuScroll.SetChild(_menu);
        var sidebar = Adw.ToolbarView.New();
        sidebar.AddTopBar(Adw.HeaderBar.New());
        sidebar.SetContent(menuScroll);

        _listHost.SetSizeRequest(340, -1);
        var content = Gtk.Paned.New(Gtk.Orientation.Horizontal);
        content.SetStartChild(_listHost);
        content.SetEndChild(_pane.Root);
        content.SetResizeStartChild(false);
        content.SetShrinkStartChild(false);
        content.SetShrinkEndChild(false);
        content.SetPosition(400);

        var split = Adw.NavigationSplitView.New();
        split.SetMinSidebarWidth(240);
        split.SetMaxSidebarWidth(300);
        split.SetSidebar(Adw.NavigationPage.New(sidebar, "Wino Mail"));
        split.SetContent(Adw.NavigationPage.New(content, ShellModeActivator.ModeTitle(WinoApplicationMode.Mail)));
        Root = split;

        Bind.Bind(nameof(WinoAppShellViewModel.CurrentMenu), static vm => vm.CurrentMenu, _ => QueueRebuild());
        Bind.Bind(nameof(WinoAppShellViewModel.SelectedMenuItem), static vm => vm.SelectedMenuItem, _ => QueueRebuild());
    }

    public override Gtk.Widget Root { get; }
    public WinoApplicationMode? ActiveMode => _modes.ActiveMode;
    public object? ContentViewModel { get; private set; }

    public async Task SetContentAsync(IWinoView view, object? parameter)
    {
        if (IsReleased)
        {
            await view.ReleaseAsync();
            return;
        }

        var version = ++_contentVersion;
        var previous = _content;
        _listHost.SetChild(view.Root);
        _content = view;
        ContentViewModel = view.AssociatedViewModel;
        if (previous is not null) await ReleaseContentAsync(previous);
        if (version != _contentVersion) return;
        await view.ActivateAsync(NavigationMode.New, parameter);
    }

    public async Task ClearContentAsync()
    {
        ++_contentVersion;
        var previous = _content;
        _content = null;
        ContentViewModel = null;
        if (!IsReleased) _listHost.SetChild(null);
        if (previous is not null) await ReleaseContentAsync(previous);
    }

    public async Task ActivateModeAsync(WinoApplicationMode mode, ShellModeActivationContext? context)
    {
        if (IsReleased || mode != WinoApplicationMode.Mail) return;
        await _modes.ActivateAsync(mode, context, _navigation.ClearShellContentAsync, Dispatcher);
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(mode, null);
        _navigation.AttachRenderingHost(_pane);
        _navigation.Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame);
        return Task.CompletedTask;
    }

    // Mode activation navigates, so it follows OnPageLoaded as on the other heads.
    protected override Task LoadedAsync()
        => ActivateModeAsync(WinoApplicationMode.Mail, new ShellModeActivationContext { IsInitialActivation = true });

    protected override async Task DeactivateAsync()
    {
        _navigation.DetachRenderingHost(_pane);
        ClearSubscriptions();
        await ClearContentAsync();
        await _pane.ReleaseAsync();
        ViewModel.OnNavigatedFrom(NavigationMode.Back, null);
        ViewModel.ShutdownProviders();
    }

    private async Task ReleaseContentAsync(IWinoView view)
    {
        try { await view.ReleaseAsync(); }
        catch (Exception exception) { ReportError(exception); }
    }

    // The menu changes in bursts (a folder load raises many notifications), so one rebuild runs per main-loop turn.
    private void QueueRebuild()
    {
        if (_rebuildQueued || IsReleased) return;
        _rebuildQueued = true;
        GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, () =>
        {
            _rebuildQueued = false;
            if (!IsReleased) Rebuild();
            return false;
        });
    }

    private void Rebuild()
    {
        ClearSubscriptions();
        _menu.RemoveAll();
        _rows.Clear();
        if (ViewModel.CurrentMenu is not { } menu) return;

        Watch(menu.Items);
        Gtk.ListBoxRow? selected = null;
        foreach (var (item, depth) in ShellMenuRowPresentation.Flatten(menu.Items, includeNewItems: false))
        {
            Watch(item);
            Watch(ShellMenuItemRules.Children(item));
            var row = ShellMenuRow.Create(item, ShellMenuRowPresentation.Describe(item, depth, null));
            _menu.Append(row);
            _rows.Add(item);
            if (ReferenceEquals(item, ViewModel.SelectedMenuItem)) selected = row;
        }

        if (selected is not null) _menu.SelectRow(selected);
        else _menu.UnselectAll();
    }

    private async void Invoke(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        var item = _rows[index];
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
            if (ViewModel.HandlesSelection && ShellMenuItemRules.SelectsOnInvoked(item) && item is not IBaseFolderMenuItem)
                ViewModel.SelectedMenuItem = item;
            await ViewModel.InvokeMenuItemAsync(item);
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "A shell menu item failed."); }
        // The list selected the clicked row itself; show the ViewModel's selection again.
        finally { QueueRebuild(); }
    }

    private void Watch(object? source)
    {
        if (source is INotifyCollectionChanged collection)
        {
            NotifyCollectionChangedEventHandler handler = (_, _) => QueueRebuild();
            collection.CollectionChanged += handler;
            _subscriptions.Add(() => collection.CollectionChanged -= handler);
        }
        else if (source is INotifyPropertyChanged item)
        {
            PropertyChangedEventHandler handler = (_, _) => QueueRebuild();
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
