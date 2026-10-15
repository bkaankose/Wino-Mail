using System.Collections.Specialized;
using System.ComponentModel;
using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.Controls.UIKit.Shell;
using Wino.Mail.UIKit.Pad.Mail;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Pad.Shell;

/// <summary>
/// The sidebar column (the Mac ShellSidebarViewController): the current mode's menu with the Windows row anatomy
/// (<see cref="WinoShellRowCell"/>: New item, account rows with avatar or provider logo, folder rows with special-folder
/// glyphs and accent unread pills, section headers, linked accounts, "More", the attention row) over the blurred theme
/// backdrop, and the <see cref="WinoModeSwitcher"/> card at its foot (Mail, Calendar, Contacts, To Do; Settings is never a
/// mode). Rows follow their items live (unread counts, sync, expansion); long press opens the shared context menus; folder
/// rows take dropped mails. The shell owns the ViewModel; this controller presents and forwards taps.
/// </summary>
internal sealed partial class PadSidebarViewController : UIViewController
{
    /// <summary>The trailing area of a parent row that toggles its children instead of selecting it.</summary>
    private const double ChevronHitWidth = 44;

    private readonly IPictureStorageService? _pictures;
    private readonly IDispatcher _dispatcher;
    private readonly Action<Exception> _error;
    private readonly Action<IMenuItem> _invoke;
    private readonly Action<WinoApplicationMode> _selectMode;
    private readonly Action<IMenuItem> _attention;
    private readonly PadSidebarMenuContext _menus;
    private readonly UITableView _table = new(CGRect.Empty, UITableViewStyle.Plain) { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly WinoModeSwitcher _switcher = new();
    // The per-mode content slot: a mode page's own sidebar content in place of the menu rows (Calendar's month picker
    // and calendars). It covers the rows' area above the mode switcher.
    private readonly UIView _modeContentHost = new() { TranslatesAutoresizingMaskIntoConstraints = false, BackgroundColor = UIColor.Clear, Hidden = true };
    private UIViewController? _modeContent;
    private readonly List<(IMenuItem Item, int Depth)> _rows = [];
    private readonly List<INotifyCollectionChanged> _collections = [];
    private readonly List<INotifyPropertyChanged> _properties = [];
    private SidebarSource? _source;
    private SidebarDropDelegate? _dropDelegate;
    private IList<IMenuItem>? _items;
    private bool _handlesSelection;
    private IMenuItem? _selectedItem;
    private bool _reloadQueued;
    private bool _disposed;

    public PadSidebarViewController(IPictureStorageService? pictures, IDispatcher dispatcher, Action<Exception> error, Action<IMenuItem> invoke,
        Action<WinoApplicationMode> selectMode, Action<IMenuItem> attention, PadSidebarMenuContext menus)
    {
        _pictures = pictures;
        _dispatcher = dispatcher;
        _error = error;
        _invoke = invoke;
        _selectMode = selectMode;
        _attention = attention;
        _menus = menus;
    }

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = UIColor.Clear };
        WinoLayout.Fill(new WinoBarBackdropView(), root);
        WinoLayout.Fill(new WinoSurfaceView { Fill = WinoThemeSurfaces.NavigationFill, UserInteractionEnabled = false }, root);

        _table.BackgroundColor = UIColor.Clear;
        _table.SeparatorStyle = UITableViewCellSeparatorStyle.None;
        _table.SectionHeaderTopPadding = 0;
        _table.EstimatedRowHeight = 0;
        _table.AllowsFocus = true;
        _table.RegisterClassForCellReuse(typeof(WinoShellRowCell), WinoShellRowCell.ReuseIdentifier);
        _table.RegisterClassForCellReuse(typeof(SeparatorCell), SeparatorCell.Identifier);
        _source = new SidebarSource(this);
        _table.Source = _source;
        _dropDelegate = new SidebarDropDelegate(this);
        _table.DropDelegate = _dropDelegate;
        WinoAccessibility.Label(_table, Translator.KeyboardShortcuts_ModeMail);

        // A tap on a parent row's trailing chevron toggles it instead of selecting (the Mac chevron area).
        var chevronTap = new UITapGestureRecognizer(ChevronTapped) { CancelsTouchesInView = true };
        chevronTap.ShouldBegin = recognizer => ChevronRowAt(recognizer.LocationInView(_table)) is not null;
        _table.AddGestureRecognizer(chevronTap);

        _switcher.ModeSelected += (_, mode) => _selectMode(mode);
        root.AddSubview(_table);
        root.AddSubview(_modeContentHost);
        root.AddSubview(_switcher);
        _switcher.TranslatesAutoresizingMaskIntoConstraints = false;
        var guide = root.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            _table.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 4),
            _table.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor),
            _table.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor),
            _table.BottomAnchor.ConstraintEqualTo(_switcher.TopAnchor, -6),
            _modeContentHost.TopAnchor.ConstraintEqualTo(_table.TopAnchor),
            _modeContentHost.LeadingAnchor.ConstraintEqualTo(_table.LeadingAnchor),
            _modeContentHost.TrailingAnchor.ConstraintEqualTo(_table.TrailingAnchor),
            _modeContentHost.BottomAnchor.ConstraintEqualTo(_table.BottomAnchor),
            _switcher.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, 10),
            _switcher.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -10),
            _switcher.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor, -10)
        ]);
        View = root;
#if DEBUG
        RegisterDebugCommands();
#endif
    }

    /// <summary>Replaces the menu shown in the sidebar.</summary>
    public void Bind(ShellMenu? menu)
    {
        _ = View;
        _handlesSelection = menu?.HandlesSelection == true;
        Rebind(menu?.Items);
    }

    /// <summary>The mode page's sidebar content on screen in place of the menu rows, or null.</summary>
    public UIViewController? ModeContent => _modeContent;

    /// <summary>
    /// Shows <paramref name="content"/> in place of the menu rows, above the mode switcher, or restores the rows when
    /// null. The previous content is removed, not disposed: its page owns it.
    /// </summary>
    public void SetModeContent(UIViewController? content)
    {
        _ = View;
        if (ReferenceEquals(_modeContent, content)) return;
        if (_modeContent is { } previous)
        {
            previous.WillMoveToParentViewController(null);
            previous.View?.RemoveFromSuperview();
            previous.RemoveFromParentViewController();
        }
        _modeContent = content;
        if (content is not null)
        {
            AddChildViewController(content);
            if (content.View is { } view) WinoLayout.Fill(view, _modeContentHost);
            content.DidMoveToParentViewController(this);
        }
        _modeContentHost.Hidden = content is null;
        _table.Hidden = content is not null;
    }

    /// <summary>Highlights the mode segment; the switch itself goes through the shell.</summary>
    public void SetMode(WinoApplicationMode mode)
    {
        if (mode != WinoApplicationMode.Settings) _switcher.Selected = mode;
    }

    /// <summary>Reflects the ViewModel's selected menu item without invoking it.</summary>
    public void SetSelectedItem(IMenuItem? item)
    {
        _selectedItem = item;
        if (!IsViewLoaded) return;

        // A folder inside a collapsed parent: open the parents so the selection shows.
        bool expanded = false;
        for (var parent = item?.ParentMenuItem; parent is not null; parent = parent.ParentMenuItem)
        {
            if (parent.IsExpanded || parent is IAccountMenuItem || parent is not MenuItemBase menu) continue;
            menu.IsExpanded = true;
            expanded = true;
        }
        if (expanded) RebuildRows();
        ApplySelection();
    }

    private void ApplySelection()
    {
        var index = _selectedItem is null || !_handlesSelection ? -1 : _rows.FindIndex(row => ReferenceEquals(row.Item, _selectedItem));
        foreach (var path in _table.IndexPathsForSelectedRows ?? [])
            if (path.Row != index) _table.DeselectRow(path, false);
        if (index >= 0) _table.SelectRow(NSIndexPath.FromRowSection(index, 0), false, UITableViewScrollPosition.None);
    }

    private bool IsSelectable(IMenuItem item)
        => _handlesSelection && ShellMenuItemRules.IsInteractive(item) && ShellMenuItemRules.SelectsOnInvoked(item) && ShellMenuItemRules.IsEnabled(item);

    private static bool HasChildren(IMenuItem item) => ShellMenuRowPresentation.HasChildren(item);

    #region Rows

    private void Rebind(IList<IMenuItem>? items)
    {
        Detach();
        _items = items;
        Observe(items);
        RebuildRows();
    }

    private void Observe(IEnumerable<IMenuItem>? items)
    {
        if (items is INotifyCollectionChanged changed) { _collections.Add(changed); changed.CollectionChanged += CollectionChanged; }
        foreach (var item in items ?? [])
        {
            if (item is INotifyPropertyChanged property) { _properties.Add(property); property.PropertyChanged += PropertyChanged; }
            Observe(ShellMenuItemRules.Children(item));
        }
    }

    private void Detach()
    {
        foreach (var collection in _collections) collection.CollectionChanged -= CollectionChanged;
        foreach (var property in _properties) property.PropertyChanged -= PropertyChanged;
        _collections.Clear();
        _properties.Clear();
    }

    /// <summary>Flattens the visible tree: accounts always show their folders, other parents while expanded.</summary>
    private void RebuildRows()
    {
        _rows.Clear();
        Add(_items, 0);
        _table.ReloadData();
        ApplySelection();
    }

    private void Add(IEnumerable<IMenuItem>? items, int depth)
    {
        foreach (var item in items?.ToArray() ?? [])
        {
            if (!ShellMenuItemRules.IsShown(item)) continue;
            _rows.Add((item, depth));
            if (item is IAccountMenuItem || item.IsExpanded) Add(ShellMenuItemRules.Children(item), depth + 1);
        }
    }

    private async void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        // Menus are rebuilt in bursts; coalesce into one reload per dispatcher turn.
        if (_reloadQueued) return;
        _reloadQueued = true;
        try
        {
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                _reloadQueued = false;
                if (!_disposed) Rebind(_items);
            });
        }
        catch (Exception exception) { if (!_disposed) _error(exception); }
    }

    private async void PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        try
        {
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                if (_disposed || sender is not IMenuItem item) return;
                switch (args.PropertyName)
                {
                    case nameof(IMenuItem.IsExpanded):
                        RebuildRows();
                        return;
                    case nameof(MenuItemBase.IsEnabled) when item is NewContactMenuItem or NewAddressListMenuItem:
                        Rebind(_items);
                        return;
                    default:
                        Refresh(item);
                        return;
                }
            });
        }
        catch (Exception exception) { if (!_disposed) _error(exception); }
    }

    /// <summary>Reconfigures the visible cell of <paramref name="item"/> (unread count, sync, attention).</summary>
    private void Refresh(IMenuItem item)
    {
        var index = _rows.FindIndex(row => ReferenceEquals(row.Item, item));
        if (index < 0 || _table.CellAt(NSIndexPath.FromRowSection(index, 0)) is not WinoShellRowCell cell) return;
        Configure(cell, item, _rows[index].Depth);
    }

    private static WinoShellRowKind KindOf(IMenuItem item) => ShellMenuRowPresentation.KindOf(item) switch
    {
        ShellMenuRowKind.NewItem => WinoShellRowKind.NewItem,
        ShellMenuRowKind.Header => WinoShellRowKind.Header,
        ShellMenuRowKind.Account => WinoShellRowKind.Account,
        _ => WinoShellRowKind.Folder
    };

    private static double HeightOf(IMenuItem item) => item switch
    {
        SeperatorItem => 9,
        FixAccountIssuesMenuItem => 58,
        _ => WinoShellRowCell.HeightFor(KindOf(item))
    };

    /// <summary>Configures the row from the shared <see cref="ShellMenuRowPresentation"/> (the Windows pane templates).</summary>
    private void Configure(WinoShellRowCell cell, IMenuItem item, int depth)
    {
        cell.Item = item;
        cell.AccessoryView = null;
        var row = ShellMenuRowPresentation.Describe(item, depth, _pictures);
        switch (row.Kind)
        {
            case ShellMenuRowKind.NewItem:
                cell.ConfigureNewItem(row.Title, row.Glyph);
                break;
            case ShellMenuRowKind.Header:
                cell.ConfigureHeader(row.Title);
                break;
            case ShellMenuRowKind.Account when row.AccountIcon is { } icon:
                cell.ConfigureAccount(icon, row.Title, row.Subtitle, row.UnreadCount);
                cell.SetSync(row.ShowsSync, row.IsSyncIndeterminate, row.SyncValue);
                if (row.IsAttentionRequired) cell.AccessoryView = AttentionButton(item);
                break;
            default:
                cell.ConfigureFolder(row.Glyph, row.Title, row.UnreadCount, row.Indent, row.IsExpanded);
                break;
        }
        cell.Highlightable = row.IsHighlightable;
        cell.ShowsIndicator = row.ShowsIndicator;
        cell.ContentView.Alpha = row.IsEnabled ? 1 : 0.45f;
    }

    private UIButton AttentionButton(IMenuItem item)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(WinoIconGlyph.Warning, 18, WinoStyle.Caution, Translator.Info_AccountAttentionRequiredAction);
        var button = new UIButton(new CGRect(0, 0, 36, 36)) { Configuration = configuration };
        WinoAccessibility.Label(button, Translator.Info_AccountAttentionRequiredAction);
        WinoAccessibility.Help(button, Translator.Info_AccountAttentionRequiredClickableMessage);
        button.PrimaryActionTriggered += (_, _) => _attention(item);
        return button;
    }

    #endregion

    #region Taps

    private void RowTapped(int index)
    {
        if (index < 0 || index >= _rows.Count) return;

        var item = _rows[index].Item;
        if (!ShellMenuItemRules.IsInteractive(item) || !ShellMenuItemRules.IsEnabled(item))
        {
            ApplySelection();
            return;
        }
        if (IsSelectable(item)) _selectedItem = item;
        else
        {
            // Rows that do not select still invoke; parents toggle their children.
            ApplySelection();
            if (HasChildren(item) && item is MenuItemBase parent && item is not IAccountMenuItem) parent.IsExpanded = !parent.IsExpanded;
        }
        Invoke(item);
    }

    private void Invoke(IMenuItem item)
    {
        try { _invoke(item); }
        catch (Exception exception) { _error(exception); }
    }

    private (IMenuItem Item, int Index)? ChevronRowAt(CGPoint point)
    {
        if (_table.IndexPathForRowAtPoint(point) is not { } path || path.Row >= _rows.Count) return null;
        var item = _rows[path.Row].Item;
        if (item is IAccountMenuItem || !HasChildren(item) || !IsSelectable(item)) return null;
        return point.X >= _table.Bounds.Width - ChevronHitWidth ? (item, path.Row) : null;
    }

    private void ChevronTapped(UITapGestureRecognizer recognizer)
    {
        if (ChevronRowAt(recognizer.LocationInView(_table)) is not { Item: MenuItemBase parent }) return;
        parent.IsExpanded = !parent.IsExpanded;
    }

    #endregion

    #region Mail drop (Windows ShellMenuTemplates ItemDragOverFolder / ItemDroppedOnFolder)

    private int _dropRow = -1;

    private IBaseFolderMenuItem? FolderAt(NSIndexPath? path)
        => path is not null && path.Row >= 0 && path.Row < _rows.Count && _rows[path.Row].Item is IBaseFolderMenuItem folder && ShellMenuItemRules.IsEnabled(folder)
            ? folder
            : null;

    private void SetDropRow(int row)
    {
        if (row == _dropRow) return;
        if (_dropRow >= 0 && _table.CellAt(NSIndexPath.FromRowSection(_dropRow, 0)) is WinoShellRowCell previous) previous.IsDropTarget = false;
        _dropRow = row;
        if (row >= 0 && _table.CellAt(NSIndexPath.FromRowSection(row, 0)) is WinoShellRowCell current) current.IsDropTarget = true;
    }

    private UITableViewDropProposal ValidateDrop(IUIDropSession session, NSIndexPath? path)
    {
        var mails = MailDragPayload.From(session);
        var folder = FolderAt(path);
        if (mails.Count == 0 || folder is null || path is null
            || !MailDragPayload.CanDropOn(folder, ReferenceEquals(_selectedItem, folder), mails))
        {
            SetDropRow(-1);
            return new UITableViewDropProposal(UIDropOperation.Forbidden);
        }
        SetDropRow(path.Row);
        return new UITableViewDropProposal(UIDropOperation.Move, UITableViewDropIntent.InsertIntoDestinationIndexPath);
    }

    private void PerformDrop(IUITableViewDropCoordinator coordinator)
    {
        SetDropRow(-1);
        var mails = MailDragPayload.From(coordinator.Session);
        if (FolderAt(coordinator.DestinationIndexPath) is not { } folder || !MailDragPayload.CanDropOn(folder, ReferenceEquals(_selectedItem, folder), mails)) return;
        if (_menus.Mail() is not { } mail) return;

        // Only the mails of the folder's accounts move (Windows: other accounts' mails stay put).
        var moving = MailDragPayload.MailsFor(folder, mails);
        if (moving.Count > 0) _ = MoveAsync(mail, moving, folder);
    }

    private async Task MoveAsync(IMailShellClient mail, List<MailCopy> mails, IBaseFolderMenuItem folder)
    {
        try { await mail.PerformMoveOperationAsync(mails, folder); }
        catch (Exception exception) { _error(exception); }
    }

    private sealed class SidebarDropDelegate(PadSidebarViewController owner) : UITableViewDropDelegate
    {
        public override bool CanHandleDropSession(UITableView tableView, IUIDropSession session) => MailDragPayload.From(session).Count > 0;

        public override UITableViewDropProposal DropSessionDidUpdate(UITableView tableView, IUIDropSession session, NSIndexPath? destinationIndexPath)
            => owner.ValidateDrop(session, destinationIndexPath);

        public override void PerformDrop(UITableView tableView, IUITableViewDropCoordinator coordinator) => owner.PerformDrop(coordinator);

        public override void DropSessionDidExit(UITableView tableView, IUIDropSession session) => owner.SetDropRow(-1);

        public override void DropSessionDidEnd(UITableView tableView, IUIDropSession session) => owner.SetDropRow(-1);
    }

    #endregion

    private sealed class SidebarSource(PadSidebarViewController owner) : UITableViewSource
    {
        public override nint RowsInSection(UITableView tableview, nint section) => owner._rows.Count;

        public override nfloat GetHeightForRow(UITableView tableView, NSIndexPath indexPath)
            => (nfloat)HeightOf(owner._rows[indexPath.Row].Item);

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var (item, depth) = owner._rows[indexPath.Row];
            if (item is SeperatorItem) return tableView.DequeueReusableCell(SeparatorCell.Identifier, indexPath);

            var cell = (WinoShellRowCell)tableView.DequeueReusableCell(WinoShellRowCell.ReuseIdentifier, indexPath);
            owner.Configure(cell, item, depth);
            cell.IsDropTarget = indexPath.Row == owner._dropRow;
            return cell;
        }

        public override NSIndexPath? WillSelectRow(UITableView tableView, NSIndexPath indexPath)
        {
            var item = owner._rows[indexPath.Row].Item;
            if (owner.IsSelectable(item)) return indexPath;
            // Not a selection: run the row's action and keep the current highlight.
            owner.RowTapped(indexPath.Row);
            return null;
        }

        public override void RowSelected(UITableView tableView, NSIndexPath indexPath) => owner.RowTapped(indexPath.Row);

        public override bool ShouldHighlightRow(UITableView tableView, NSIndexPath indexPath)
            => owner._rows[indexPath.Row].Item is not (SeperatorItem or ShellSectionHeaderMenuItem);

        public override UIContextMenuConfiguration? GetContextMenuConfiguration(UITableView tableView, NSIndexPath indexPath, CGPoint point)
        {
            var item = owner._rows[indexPath.Row].Item;
            if (!ShellMenuItemRules.IsEnabled(item)) return null;
            UIMenu? menu;
            try { menu = PadSidebarContextMenus.Build(item, owner._menus); }
            catch (Exception exception) { owner._error(exception); return null; }
            return menu is null ? null : UIContextMenuConfiguration.Create(null, null, _ => menu);
        }
    }

    /// <summary>The 9pt separator row between pane groups.</summary>
    private sealed class SeparatorCell : UITableViewCell
    {
        public const string Identifier = "WinoShellSeparator";

        [Export("initWithStyle:reuseIdentifier:")]
        public SeparatorCell(UITableViewCellStyle style, NSString reuseIdentifier) : base(style, reuseIdentifier)
        {
            BackgroundColor = UIColor.Clear;
            SelectionStyle = UITableViewCellSelectionStyle.None;
            var line = new WinoSeparator();
            ContentView.AddSubview(line);
            NSLayoutConstraint.ActivateConstraints(
            [
                line.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 16),
                line.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -16),
                line.CenterYAnchor.ConstraintEqualTo(ContentView.CenterYAnchor)
            ]);
            IsAccessibilityElement = false;
        }
    }

#if DEBUG
    /// <summary>Debug bridge: "sidebar-rows" lists the rows; "sidebar-tap N" taps row N; "sidebar-menu N" lists its context menu.</summary>
    private void RegisterDebugCommands()
    {
        Infrastructure.UIKitDebugBridge.Register("sidebar-rows", _ => Task.FromResult(string.Join(" | ",
            _rows.Select((row, index) => $"{index}:{row.Item.GetType().Name}:{(row.Item as IBaseFolderMenuItem)?.FolderName ?? (row.Item as IAccountNavigationMenuItem)?.AccountName}"))));
        Infrastructure.UIKitDebugBridge.Register("sidebar-tap", args =>
        {
            if (args.Length == 0 || !int.TryParse(args[0], out var index) || index < 0 || index >= _rows.Count) return Task.FromResult("usage: sidebar-tap ROW");
            var path = NSIndexPath.FromRowSection(index, 0);
            if (_source?.WillSelectRow(_table, path) is { } selected)
            {
                _table.SelectRow(selected, false, UITableViewScrollPosition.None);
                RowTapped(index);
            }
            return Task.FromResult("ok");
        });
        Infrastructure.UIKitDebugBridge.Register("sidebar-menu", args =>
        {
            if (args.Length == 0 || !int.TryParse(args[0], out var index) || index < 0 || index >= _rows.Count) return Task.FromResult("usage: sidebar-menu ROW");
            var menu = PadSidebarContextMenus.Build(_rows[index].Item, _menus);
            return Task.FromResult(menu is null ? "no menu" : string.Join(" | ", PadSidebarContextMenus.Titles(menu)));
        });
    }
#endif

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            Detach();
            _table.Source = null;
            _table.DropDelegate = null;
            _source?.Dispose();
            _dropDelegate?.Dispose();
        }
        base.Dispose(disposing);
    }
}
