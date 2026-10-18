using System.Collections.Specialized;
using System.ComponentModel;
using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Shell;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Shell;

/// <summary>
/// How a mode's menu rows look on its <see cref="ModeMenuRootViewController"/>: the row anatomy per item and which row is
/// the surface on screen. One per mode that lists its menu in the drawer (To Do: <see cref="ToDoMenuRowStyle"/>; Contacts:
/// <see cref="PresentationMenuRowStyle"/>).
/// </summary>
internal interface IModeMenuRowStyle
{
    /// <summary>The drawer's title.</summary>
    string Title { get; }

    /// <summary>The row kind of <paramref name="item"/>, or null to leave it out.</summary>
    WinoShellRowKind? Kind(IMenuItem item);

    void Configure(WinoShellRowCell cell, IMenuItem item, int depth);

    /// <summary>True when <paramref name="item"/> is the surface the content page shows.</summary>
    bool IsCurrent(IMenuItem item, object? selectedMenuItem, object? contentViewModel);
}

/// <summary>
/// The shell drawer's content for a mode that lists its published menu (the iPhone counterpart of the iPad sidebar), the
/// way Mailboxes is Mail's: a row that selects a surface closes the drawer and goes through the shell; parents expand in
/// place; pull to refresh synchronizes the mode; the Settings row sits at the foot. Rows follow their items live (counts,
/// names, expansion). Used by To Do and Contacts; Mail keeps its richer Mailboxes, Calendar lists its calendars.
/// </summary>
internal sealed class ModeMenuRootViewController : UIViewController
{
    private readonly PhoneShellViewController _shell;
    private readonly IDispatcher _dispatcher;
    private readonly IModeMenuRowStyle _style;
    private readonly UITableView _table = new(CGRect.Empty, UITableViewStyle.Plain);
    private readonly UIRefreshControl _refresh = new();
    private readonly List<(IMenuItem Item, int Depth)> _rows = [];
    private readonly List<INotifyCollectionChanged> _collections = [];
    private readonly List<INotifyPropertyChanged> _properties = [];
    private ShellMenu? _menu;
    private object? _selected;
    private bool _reloadQueued;

    public ModeMenuRootViewController(PhoneShellViewController shell, WinoApplicationMode mode, IDispatcher dispatcher, IModeMenuRowStyle style)
    {
        _shell = shell;
        _dispatcher = dispatcher;
        _style = style;
        Mode = mode;
        NavigationItem.Title = style.Title;
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
    }

    /// <summary>The mode whose menu this root lists.</summary>
    public WinoApplicationMode Mode { get; }

    public override void LoadView()
    {
        // The drawer's blur is the surface: the rows sit on it directly, without a zone.
        var root = new UIView { BackgroundColor = UIColor.Clear };
        _table.BackgroundColor = UIColor.Clear;
        _table.SeparatorStyle = UITableViewCellSeparatorStyle.None;
        _table.TranslatesAutoresizingMaskIntoConstraints = false;
        _table.RegisterClassForCellReuse(typeof(WinoShellRowCell), WinoShellRowCell.ReuseIdentifier);
        _table.Source = new RowsSource(this);
        _table.ContentInset = new UIEdgeInsets(4, 0, 6, 0);
        _refresh.ValueChanged += (_, _) => Synchronize();
        _table.RefreshControl = _refresh;
        WinoAccessibility.Label(_table, _style.Title);
        var listHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(_table, listHost, 0, 6, 0, 6);

        var column = WinoLayout.VStack(0, listHost, DrawerSettingsRow.Create(_shell.OpenSettingsFromDrawer));
        column.Alignment = UIStackViewAlignment.Fill;
        column.TranslatesAutoresizingMaskIntoConstraints = false;
        root.AddSubview(column);
        var guide = root.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            column.TopAnchor.ConstraintEqualTo(guide.TopAnchor),
            column.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            column.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            column.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor)
        ]);
        View = root;
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        SetContentScrollView(_table, NSDirectionalRectEdge.Top);
        RebuildRows();
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        RebuildRows();
    }

    /// <summary>The shell's published menu while this root's mode is active, else null.</summary>
    public void SetMenu(ShellMenu? menu)
    {
        if (ReferenceEquals(_menu, menu)) return;
        Detach();
        _menu = menu;
        Observe(menu?.Items);
        if (IsViewLoaded) RebuildRows();
    }

    /// <summary>The shell's selected menu item.</summary>
    public void SetSelected(object? item)
    {
        _selected = item;
        QueueReload();
    }

    private void Observe(IEnumerable<IMenuItem>? items)
    {
        if (items is INotifyCollectionChanged changed) { _collections.Add(changed); changed.CollectionChanged += CollectionChanged; }
        foreach (var item in items ?? [])
        {
            if (item is INotifyPropertyChanged property) { _properties.Add(property); property.PropertyChanged += ItemChanged; }
            Observe(ShellMenuItemRules.Children(item));
        }
    }

    private void Detach()
    {
        foreach (var collection in _collections) collection.CollectionChanged -= CollectionChanged;
        foreach (var property in _properties) property.PropertyChanged -= ItemChanged;
        _collections.Clear();
        _properties.Clear();
    }

    // A changed collection can add items: observe the tree again, then rebuild.
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
        => _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            var menu = _menu;
            Detach();
            Observe(menu?.Items);
            QueueReload();
        });

    private void ItemChanged(object? sender, PropertyChangedEventArgs args) => QueueReload();

    /// <summary>Coalesces count, name and expansion changes into one reload per main-loop turn.</summary>
    private void QueueReload()
    {
        if (_reloadQueued) return;
        _reloadQueued = true;
        _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            _reloadQueued = false;
            if (IsViewLoaded) RebuildRows();
        });
    }

    private void RebuildRows()
    {
        _rows.Clear();
        Add(_menu?.Items, 0);
        _table.ReloadData();
        var contentViewModel = _shell.ContentViewModel;
        var current = _rows.FindIndex(row => _style.IsCurrent(row.Item, _selected, contentViewModel));
        if (current >= 0) _table.SelectRow(NSIndexPath.FromRowSection(current, 0), false, UITableViewScrollPosition.None);
    }

    private void Add(IEnumerable<IMenuItem>? items, int depth)
    {
        foreach (var item in items?.ToArray() ?? [])
        {
            if (!ShellMenuItemRules.IsShown(item) || (item is not SeperatorItem && _style.Kind(item) is null)) continue;
            _rows.Add((item, depth));
            if (item is IAccountMenuItem || item.IsExpanded) Add(ShellMenuItemRules.Children(item), depth + 1);
        }
    }

    private void RowTapped(IMenuItem item)
    {
        // A parent that does not select a surface (a list group) expands in place.
        if (!ShellMenuItemRules.SelectsOnInvoked(item) && ShellMenuItemRules.Children(item).Any(ShellMenuItemRules.IsShown) && item is MenuItemBase parent)
        {
            parent.IsExpanded = !parent.IsExpanded;
            RebuildRows();
            return;
        }
        _shell.OpenFromDrawer(item);
    }

    private void Synchronize()
    {
        _ = _shell.SynchronizeAsync();
        NSTimer.CreateScheduledTimer(0.8, _ => { if (_refresh.Refreshing) _refresh.EndRefreshing(); });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Detach();
        base.Dispose(disposing);
    }

#if DEBUG
    /// <summary>Debug: the rows as type names with their depth.</summary>
    internal string DebugDescribe() => string.Join(" | ", _rows.Select(row => $"{new string('-', row.Depth)}{row.Item.GetType().Name}"));

    /// <summary>Debug: taps row <paramref name="index"/> as a touch does.</summary>
    internal string DebugTap(int index)
    {
        if (index < 0 || index >= _rows.Count) return "no row " + index;
        var item = _rows[index].Item;
        RowTapped(item);
        return "ok " + item.GetType().Name;
    }
#endif

    private sealed class RowsSource(ModeMenuRootViewController owner) : UITableViewSource
    {
        public override nint RowsInSection(UITableView tableview, nint section) => owner._rows.Count;

        public override nfloat GetHeightForRow(UITableView tableView, NSIndexPath indexPath)
        {
            var item = owner._rows[(int)indexPath.Row].Item;
            if (item is SeperatorItem) return 13;
            return (nfloat)WinoShellRowCell.HeightFor(owner._style.Kind(item) ?? WinoShellRowKind.Folder);
        }

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var (item, depth) = owner._rows[(int)indexPath.Row];
            if (item is SeperatorItem) return SeparatorCell(tableView);
            var cell = (WinoShellRowCell)tableView.DequeueReusableCell(WinoShellRowCell.ReuseIdentifier, indexPath);
            cell.Item = item;
            owner._style.Configure(cell, item, depth);
            cell.Highlightable = ShellMenuItemRules.IsHighlightable(item);
            cell.ShowsIndicator = ShellMenuItemRules.ShowsIndicator(item);
            cell.ContentView.Alpha = ShellMenuItemRules.IsEnabled(item) ? 1 : 0.45f;
            return cell;
        }

        private static UITableViewCell SeparatorCell(UITableView tableView)
        {
            const string identifier = "ModeMenuSeparator";
            if (tableView.DequeueReusableCell(identifier) is { } reused) return reused;
            var cell = new UITableViewCell(UITableViewCellStyle.Default, identifier)
            {
                BackgroundColor = UIColor.Clear,
                SelectionStyle = UITableViewCellSelectionStyle.None,
                IsAccessibilityElement = false
            };
            var line = new WinoSeparator();
            cell.ContentView.AddSubview(line);
            NSLayoutConstraint.ActivateConstraints(
            [
                line.LeadingAnchor.ConstraintEqualTo(cell.ContentView.LeadingAnchor, 12),
                line.TrailingAnchor.ConstraintEqualTo(cell.ContentView.TrailingAnchor, -12),
                line.CenterYAnchor.ConstraintEqualTo(cell.ContentView.CenterYAnchor)
            ]);
            return cell;
        }

        public override NSIndexPath? WillSelectRow(UITableView tableView, NSIndexPath indexPath)
        {
            var item = owner._rows[(int)indexPath.Row].Item;
            return item is not SeperatorItem && ShellMenuItemRules.IsInteractive(item) && ShellMenuItemRules.IsEnabled(item) ? indexPath : null;
        }

        public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
        {
            var item = owner._rows[(int)indexPath.Row].Item;
            if (!ShellMenuItemRules.SelectsOnInvoked(item)) tableView.DeselectRow(indexPath, true);
            owner.RowTapped(item);
        }
    }
}
