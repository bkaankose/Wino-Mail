using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
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
internal sealed class ShellView
{
    private readonly IDispatcher _dispatcher;
    private readonly GtkNavigationService _navigation;
    private readonly ShellModeActivator _modes;
    private readonly Gtk.ListBox _menu = Gtk.ListBox.New();
    private readonly Adw.Bin _listHost = Adw.Bin.New();
    private readonly List<IMenuItem> _rows = [];
    private readonly List<Action> _subscriptions = [];
    private bool _rebuildQueued;
    private bool _released;

    public ShellView(IServiceProvider services, IDispatcher dispatcher, GtkNavigationService navigation)
    {
        _dispatcher = dispatcher;
        _navigation = navigation;
        ViewModel = services.GetRequiredService<WinoAppShellViewModel>();
        ViewModel.Dispatcher = dispatcher;
        _modes = new ShellModeActivator(ViewModel, ViewModel.StatePersistenceService, exception => Serilog.Log.Error(exception, "Shell mode activation failed."));
        Reader = PageScopes.Create<MailReaderView>(services);

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
        content.SetEndChild(Reader.Root);
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

        ViewModel.PropertyChanged += ViewModelChanged;
    }

    public Gtk.Widget Root { get; }
    public WinoAppShellViewModel ViewModel { get; }
    public MailReaderView Reader { get; }
    public WinoApplicationMode? ActiveMode => _modes.ActiveMode;

    public async Task ActivateAsync()
    {
        ViewModel.OnNavigatedTo(NavigationMode.New, null);
        ViewModel.OnPageLoaded();
        await ActivateModeAsync(WinoApplicationMode.Mail, new ShellModeActivationContext { IsInitialActivation = true });
    }

    public async Task ActivateModeAsync(WinoApplicationMode mode, ShellModeActivationContext? context)
    {
        if (_released || mode != WinoApplicationMode.Mail) return;
        await _modes.ActivateAsync(mode, context, _navigation.ClearShellContentAsync, _dispatcher);
    }

    public void ShowMailList(MailListView? list) => _listHost.SetChild(list?.Root);

    public async Task ReleaseAsync()
    {
        if (_released) return;
        _released = true;
        ViewModel.PropertyChanged -= ViewModelChanged;
        ClearSubscriptions();
        await Reader.ReleaseAsync();
        ViewModel.OnNavigatedFrom(NavigationMode.Back, null);
        ViewModel.ShutdownProviders();
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(WinoAppShellViewModel.CurrentMenu) or nameof(WinoAppShellViewModel.SelectedMenuItem)) QueueRebuild();
    }

    // The menu changes in bursts (a folder load raises many notifications), so one rebuild runs per main-loop turn.
    private void QueueRebuild()
    {
        if (_rebuildQueued || _released) return;
        _rebuildQueued = true;
        GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, () =>
        {
            _rebuildQueued = false;
            if (!_released) Rebuild();
            return false;
        });
    }

    private void Rebuild()
    {
        ClearSubscriptions();
        _menu.RemoveAll();
        _rows.Clear();
        if (ViewModel.CurrentMenu is not { } menu) return;

        Observe(menu.Items);
        Gtk.ListBoxRow? selected = null;
        foreach (var (item, depth) in ShellMenuRowPresentation.Flatten(menu.Items, includeNewItems: false))
        {
            Observe(item);
            Observe(ShellMenuItemRules.Children(item));
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

    private void Observe(object? source)
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
