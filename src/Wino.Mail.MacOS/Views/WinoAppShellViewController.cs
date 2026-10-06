using AppKit;
using Foundation;
using System.Collections.Specialized;
using System.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;
using Wino.Shell.ViewModels;

namespace Wino.Mail.MacOS.Views;

public sealed class WinoAppShellViewController : WinoViewController<WinoAppShellViewModel>
{
    private readonly MailAppShellViewModel _mail;
    private readonly INavigationService _navigation;
    private readonly NSView _content = new();
    private readonly NSOutlineView _outline = new();
    private readonly MenuRows _rows;
    private readonly MenuSelection _selection;
    private NSViewController? _child;

    public WinoAppShellViewController(WinoAppShellViewModel viewModel, MailAppShellViewModel mail, INavigationService navigation,
        IDispatcher dispatcher, IWinoLogger logger) : base(viewModel, dispatcher, logger)
    {
        _mail = mail;
        _navigation = navigation;
        _mail.Dispatcher = dispatcher;
        _rows = new MenuRows(this);
        _selection = new MenuSelection(this);
    }

    public override void LoadView()
    {
        var column = new NSTableColumn("folders") { Width = 260 };
        _outline.AddColumn(column);
        _outline.OutlineTableColumn = column;
        _outline.HeaderView = null;
        _outline.RowHeight = 30;
        _outline.DataSource = _rows;
        _outline.Delegate = _selection;
        var scroll = new NSScrollView { DocumentView = _outline, HasVerticalScroller = true };
        var about = new NSButton { Title = Translator.SettingsAbout_Title };
        EventHandler showAbout = (_, _) => _navigation.Navigate(WinoPage.AboutPage);
        about.Activated += showAbout;
        Bindings.Own(new ActionDisposable(() => about.Activated -= showAbout));
        Bindings.Own(_rows);
        Bindings.Own(_selection);
        Bindings.Own(new PropertyBinding<WinoAppShellViewModel, Wino.Core.Domain.Models.Navigation.ShellMenu?>(ViewModel,
            nameof(ViewModel.CurrentMenu), vm => vm.CurrentMenu, menu => _rows.Bind(menu?.Items), Dispatcher, ReportError));
        View = new WinoAppShell(Layout.Stack(NSUserInterfaceLayoutOrientation.Vertical, about, scroll), _content);
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(mode, parameter!);
        ViewModel.SetCurrentMode(WinoApplicationMode.Mail);
        _ = ViewModel.GetProvider(WinoApplicationMode.Mail);
        await _mail.InitializeNavigationAsync(mode, parameter!);
        ViewModel.SetShellMenu(_mail);
    }

    public async Task SetContentAsync(NSViewController controller, object? parameter)
    {
        if (_child is IWinoViewController previous) await previous.ReleaseAsync();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            _child?.View.RemoveFromSuperview();
            _child?.Dispose();
            _child = controller;
            Layout.Fill(controller.View, _content);
        });
        if (controller is IWinoViewController next) await next.ActivateAsync(NavigationMode.New, parameter);
    }

    public async Task ClearContentAsync()
    {
        if (_child is IWinoViewController previous) await previous.ReleaseAsync();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            _child?.View.RemoveFromSuperview();
            _child?.Dispose();
            _child = null;
        });
    }

    public override async Task ReleaseAsync()
    {
        if (_child is IWinoViewController child) await child.ReleaseAsync();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            _child?.Dispose();
            _child = null;
            ViewModel.ShutdownProviders();
        });
        await base.ReleaseAsync();
    }

    private async void Select(IMenuItem item)
    {
        try
        {
            if (item is MenuItemBase { IsEnabled: false }) return;
            ViewModel.SelectedMenuItem = item;
            await ViewModel.InvokeMenuItemAsync(item);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    private static IEnumerable<IMenuItem> Children(IMenuItem item) => item switch
    {
        AccountMenuItem account => account.SubMenuItems,
        MergedAccountMenuItem merged => merged.SubMenuItems,
        IBaseFolderMenuItem folder => folder.SubMenuItems,
        _ => Array.Empty<IMenuItem>()
    };

    private static string Title(IMenuItem item) => item switch
    {
        AccountMenuItem account => $"{account.AccountName} ({account.UnreadItemCount})",
        MergedAccountMenuItem merged => $"{merged.MergedAccountName} ({merged.UnreadItemCount})",
        IBaseFolderMenuItem folder => folder.ShowUnreadCount ? $"{folder.FolderName} ({folder.UnreadItemCount})" : folder.FolderName,
        _ => item.ToString() ?? string.Empty
    };

    private sealed class Node(IMenuItem item) : NSObject
    {
        public IMenuItem Item { get; } = item;
    }

    private sealed class MenuRows(WinoAppShellViewController owner) : NSOutlineViewDataSource
    {
        private readonly Dictionary<IMenuItem, Node> _nodes = new();
        private readonly List<INotifyCollectionChanged> _collections = new();
        private readonly List<INotifyPropertyChanged> _properties = new();
        private IList<IMenuItem>? _items;
        private bool _disposed;
        public void Bind(IList<IMenuItem>? items)
        {
            Detach();
            _items = items;
            Observe(items);
            owner._outline.ReloadData();
            var live = _properties.OfType<IMenuItem>().ToHashSet();
            foreach (var item in _nodes.Keys.Where(item => !live.Contains(item)).ToList())
            {
                _nodes[item].Dispose();
                _nodes.Remove(item);
            }
            foreach (var node in _nodes.Values.Where(node => node.Item.IsExpanded)) owner._outline.ExpandItem(node);
        }
        private void Observe(IEnumerable<IMenuItem>? items)
        {
            if (items is INotifyCollectionChanged changed) { _collections.Add(changed); changed.CollectionChanged += CollectionChanged; }
            foreach (var item in items ?? Array.Empty<IMenuItem>())
            {
                if (!_nodes.ContainsKey(item)) _nodes.Add(item, new Node(item));
                if (item is INotifyPropertyChanged property) { _properties.Add(property); property.PropertyChanged += PropertyChanged; }
                Observe(Children(item));
            }
        }
        private void Detach()
        {
            foreach (var collection in _collections) collection.CollectionChanged -= CollectionChanged;
            foreach (var property in _properties) property.PropertyChanged -= PropertyChanged;
            _collections.Clear(); _properties.Clear();
        }
        private async void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            try { await owner.Dispatcher.ExecuteOnUIThread(() => { if (!_disposed) Bind(_items); }); }
            catch (Exception exception) { if (!_disposed) owner.ReportError(exception); }
        }
        private async void PropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            try { await owner.Dispatcher.ExecuteOnUIThread(() => { if (!_disposed && sender is IMenuItem item && _nodes.TryGetValue(item, out var node)) owner._outline.ReloadItem(node, false); }); }
            catch (Exception exception) { if (!_disposed) owner.ReportError(exception); }
        }
        private IList<IMenuItem> Items(NSObject? item)
        {
            var items = item is Node node ? Children(node.Item) : _items ?? new List<IMenuItem>();
            // Foundation presents account/folder routes. Composer and the other command
            // surfaces remain explicitly tracked in the presentation roadmap.
            return items.Where(value => value is IAccountMenuItem or IBaseFolderMenuItem).ToList();
        }
        public override nint GetChildrenCount(NSOutlineView outlineView, NSObject? item) => Items(item).Count;
        public override NSObject GetChild(NSOutlineView outlineView, nint childIndex, NSObject? item) => _nodes[Items(item)[(int)childIndex]];
        public override bool ItemExpandable(NSOutlineView outlineView, NSObject item) => Items(item).Count > 0;
        public override NSObject GetObjectValue(NSOutlineView outlineView, NSTableColumn? tableColumn, NSObject item) => new NSString(Title(((Node)item).Item));
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed = true;
                Detach();
                owner._outline.DataSource = null;
                owner._outline.Delegate = null;
                foreach (var node in _nodes.Values) node.Dispose();
                _nodes.Clear();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class MenuSelection(WinoAppShellViewController owner) : NSOutlineViewDelegate
    {
        public override void SelectionDidChange(NSNotification notification)
        {
            if (owner._outline.SelectedRow >= 0 && owner._outline.ItemAtRow(owner._outline.SelectedRow) is Node node)
                owner.Select(node.Item);
        }
    }
}
