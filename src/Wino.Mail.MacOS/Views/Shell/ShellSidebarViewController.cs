using System.Collections.Specialized;
using System.ComponentModel;
using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Shell;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Shell;

/// <summary>
/// The shell pane: the current mode's menu rendered with the Windows row anatomy (New item,
/// accounts, folders, section headers, calendars, task lists, contact filters) over the blurred
/// theme backdrop, and the mode switcher card at the bottom. The shell owns the ViewModel
/// lifetime; this controller only presents and forwards clicks.
/// </summary>
internal sealed partial class ShellSidebarViewController : NSViewController
{
    private readonly IDispatcher _dispatcher;
    private readonly Action<Exception> _error;
    private readonly Action<IMenuItem> _invoke;
    private readonly Action<WinoApplicationMode> _selectMode;
    private readonly Action<IMenuItem> _attention;
    private readonly ShellPaneContext _context;
    private readonly ShellSidebarMenuContext? _menus;
    private readonly ShellOutlineView _outline = new();
    private readonly NSMenu _contextMenu = new() { AutoEnablesItems = false };
    private readonly Rows _rows;
    private readonly Selection _selection;
    private readonly WinoModeSwitcher _switcher = new();
    private bool _applyingSelection;
    private bool _handlesSelection;
    private IMenuItem? _selectedItem;
    private nint _selectionHandledRow = -1;

    public ShellSidebarViewController(IDispatcher dispatcher, Action<Exception> error, Action<IMenuItem> invoke,
        Action<WinoApplicationMode> selectMode, Action<IMenuItem> attention, ShellPaneContext context, ShellSidebarMenuContext? menus = null)
    {
        _menus = menus;
        _dispatcher = dispatcher;
        _error = error;
        _invoke = invoke;
        _selectMode = selectMode;
        _attention = attention;
        _context = context;
        _rows = new Rows(this);
        _selection = new Selection(this);
    }

    public override void LoadView()
    {
        var column = new NSTableColumn("menu") { ResizingMask = NSTableColumnResizing.Autoresizing };
        _outline.AddColumn(column);
        _outline.OutlineTableColumn = column;
        _outline.HeaderView = null;
        _outline.Style = NSTableViewStyle.Plain;
        _outline.SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.Regular;
        _outline.BackgroundColor = NSColor.Clear;
        _outline.RowSizeStyle = NSTableViewRowSizeStyle.Custom;
        _outline.RowHeight = 36;
        _outline.IntercellSpacing = new CoreGraphics.CGSize(0, 2);
        _outline.FloatsGroupRows = false;
        _outline.IndentationPerLevel = (nfloat)ShellPaneRows.ChildIndent;
        _outline.IndentationMarkerFollowsCell = false;
        _outline.AutoresizesOutlineColumn = true;
        _outline.ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.FirstColumnOnly;
        _outline.AllowsEmptySelection = true;
        _outline.AllowsMultipleSelection = false;
        _outline.FocusRingType = NSFocusRingType.None;
        _outline.DataSource = _rows;
        _outline.Delegate = _selection;
        _outline.Activated += Clicked;
        // Right clicks build the row's context menu; NSTableView then outlines the clicked row natively.
        _outline.Menu = _contextMenu;
        _outline.PrepareMenu = PrepareContextMenu;
        WinoAccessibility.Label(_outline, Translator.KeyboardShortcuts_ModeMail);
        SetUpMailDrop();

        var scroll = new NSScrollView
        {
            DocumentView = _outline,
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            DrawsBackground = false,
            BorderType = NSBorderType.NoBorder,
            TranslatesAutoresizingMaskIntoConstraints = false
        };

        var root = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(new WinoPaneBackdropView(), root);
        root.AddSubview(scroll);
        root.AddSubview(_switcher);
        _switcher.ModeSelected += (_, mode) => _selectMode(mode);
        NSLayoutConstraint.ActivateConstraints(
        [
            scroll.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor, 4),
            scroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(_switcher.TopAnchor),
            _switcher.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _switcher.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            _switcher.BottomAnchor.ConstraintEqualTo(root.BottomAnchor)
        ]);
        View = root;
#if DEBUG
        RegisterDebugCommands();
#endif
    }

    /// <summary>Replaces the menu shown in the pane.</summary>
    public void Bind(ShellMenu? menu)
    {
        _handlesSelection = menu?.HandlesSelection == true;
        _rows.Bind(menu?.Items);
    }

    /// <summary>Highlights the mode segment; the switch itself goes through the shell.</summary>
    public void SetMode(WinoApplicationMode mode) => _switcher.Selected = mode;

    /// <summary>Reflects the ViewModel's selected menu item without re-invoking it.</summary>
    public void SetSelectedItem(IMenuItem? item)
    {
        _selectedItem = item;
        ApplySelection();
    }

    private void ApplySelection()
    {
        if (!ViewLoaded) return;
        _applyingSelection = true;
        try
        {
            if (_selectedItem is null || !_handlesSelection || !_rows.TryGetNode(_selectedItem, out var node))
            {
                _outline.DeselectAll(null);
                return;
            }
            for (var parent = _selectedItem.ParentMenuItem; parent is not null; parent = parent.ParentMenuItem)
                if (_rows.TryGetNode(parent, out var parentNode)) _outline.ExpandItem(parentNode);
            var row = _outline.RowForItem(node);
            if (row >= 0) _outline.SelectRow(row, false);
            else _outline.DeselectAll(null);
        }
        finally { _applyingSelection = false; }
    }

    private bool IsSelectable(IMenuItem item)
        => _handlesSelection && ShellPaneRows.IsInteractive(item) && ShellPaneRows.SelectsOnInvoked(item) && ShellPaneRows.IsEnabled(item);

    /// <summary>Mouse clicks: rows that do not select still invoke, and parents toggle their children.</summary>
    private void Clicked(object? sender, EventArgs args)
    {
        var row = _outline.ClickedRow;
        if (row < 0 || _outline.ItemAtRow(row) is not Node node) return;
        var item = node.Item;
        if (!ShellPaneRows.IsInteractive(item) || !ShellPaneRows.IsEnabled(item)) return;
        if (IsSelectable(item))
        {
            // A selection change for this click was already handled; a click on the selected row re-invokes.
            if (_selectionHandledRow == row) { _selectionHandledRow = -1; return; }
            Invoke(item);
            return;
        }
        _selectionHandledRow = -1;
        if (_rows.HasChildren(item))
        {
            if (_outline.IsItemExpanded(node)) _outline.CollapseItem(node);
            else _outline.ExpandItem(node);
        }
        Invoke(item);
    }

    private void Invoke(IMenuItem item)
    {
        try { _invoke(item); }
        catch (Exception exception) { _error(exception); }
    }

    private void ToggleExpansion(Node node)
    {
        if (_outline.IsItemExpanded(node)) _outline.CollapseItem(node);
        else _outline.ExpandItem(node);
    }

    #region Context menus

    /// <summary>Fills the shared context menu for the row under <paramref name="point"/>; false when it has no actions.</summary>
    private bool PrepareContextMenu(CGPoint point)
    {
        _contextMenu.RemoveAllItems();
        if (_menus is null || TargetAt(_outline.GetRow(point), point) is not { } target) return false;
        try { ShellSidebarContextMenus.Populate(_contextMenu, target, _menus); }
        catch (Exception exception) { _error(exception); return false; }
        return _contextMenu.Count > 0;
    }

    /// <summary>
    /// The model a right click targets. An account's calendar group draws its calendars inside one
    /// row (<see cref="ShellCalendarGroupCell"/>), so the pointer's offset below the header picks the calendar.
    /// </summary>
    private ShellSidebarMenuTarget? TargetAt(nint row, CGPoint point)
    {
        if (row < 0 || _outline.ItemAtRow(row) is not Node node) return null;
        var item = node.Item;
        if (!ShellPaneRows.IsEnabled(item)) return null;
        object? part = null;
        if (item is AccountCalendarGroupMenuItem { Parameter: { IsExpanded: true } group })
        {
            // The cell's column starts 2pt below the row top (ShellCalendarGroupCell).
            var offset = point.Y - _outline.RectForRow(row).Y - 2 - ShellCalendarGroupCell.HeaderHeight;
            if (offset >= 0)
            {
                var index = (int)(offset / ShellCalendarGroupCell.CalendarRowHeight);
                if (index < group.AccountCalendars.Count) part = group.AccountCalendars[index];
            }
        }
        return new ShellSidebarMenuTarget(item, part);
    }

    /// <summary>The pane's outline view: hands right clicks to the owner so menus follow the clicked row.</summary>
    private sealed class ShellOutlineView : NSOutlineView
    {
        public Func<CGPoint, bool>? PrepareMenu { get; set; }

        /// <summary>Raised when a drag leaves the pane or ends, to clear the drop highlight.</summary>
        public Action? DragFinished { get; set; }

        public override void DraggingExited(INSDraggingInfo? sender)
        {
            base.DraggingExited(sender);
            DragFinished?.Invoke();
        }

        public override void DraggingEnded(INSDraggingInfo sender)
        {
            base.DraggingEnded(sender);
            DragFinished?.Invoke();
        }

        /// <summary>
        /// AppKit reserves an 18pt disclosure gutter before every outline cell even though the pane
        /// never shows the outline cell (expansion uses the trailing chevron). Dropping it puts root
        /// rows right beside the selection pipe, like the Windows NavigationView, and leaves only
        /// <see cref="NSOutlineView.IndentationPerLevel"/> per nesting level.
        /// </summary>
        public override CGRect GetCellFrame(nint column, nint row)
        {
            var frame = base.GetCellFrame(column, row);
            if (row < 0 || column != 0) return frame;
            var x = Math.Min(frame.X, (nfloat)(LevelForRow(row) * IndentationPerLevel));
            return new CGRect(x, frame.Y, frame.Right - x, frame.Height);
        }

        public override NSMenu? MenuForEvent(NSEvent theEvent)
        {
            var point = ConvertPointFromView(theEvent.LocationInWindow, null);
            return PrepareMenu?.Invoke(point) == true ? base.MenuForEvent(theEvent) : null;
        }
    }

#if DEBUG
    /// <summary>
    /// Debug bridge: "sidebar-menu ROW" lists the context menu of a visible pane row,
    /// "sidebar-menu-run ROW INDEX" runs an entry (INDEX "2.1" picks a submenu entry) and
    /// "sidebar-popup ROW [NAME]" opens the menu at the row, snapshots and dismisses it.
    /// ROW "4.1" targets the second calendar inside the calendar group on row 4.
    /// </summary>
    private void RegisterDebugCommands()
    {
        Infrastructure.MacDebugBridge.Register("sidebar-menu", args =>
        {
            if (DebugMenu(args) is not { } menu) return Task.FromResult("no menu");
            var lines = new List<string>();
            Describe(menu, string.Empty, lines);
            return Task.FromResult(string.Join(" | ", lines));
        });
        Infrastructure.MacDebugBridge.Register("sidebar-menu-run", args =>
        {
            if (args.Length < 2 || DebugMenu(args) is not { } menu) return Task.FromResult("usage: sidebar-menu-run ROW INDEX");
            var path = args[1].Split('.').Select(int.Parse).ToArray();
            for (int level = 0; level < path.Length; level++)
            {
                var entries = ShellSidebarContextMenus.Entries(menu);
                if (path[level] < 0 || path[level] >= entries.Count) return Task.FromResult($"only {entries.Count} entries");
                var entry = entries[path[level]];
                if (level < path.Length - 1)
                {
                    if (entry.Submenu is not { } submenu) return Task.FromResult($"'{entry.Title}' has no submenu");
                    menu = submenu;
                    continue;
                }
                if (!entry.Enabled) return Task.FromResult($"'{entry.Title}' is disabled");
                menu.PerformActionForItem(menu.IndexOf(entry));
                return Task.FromResult("ran " + entry.Title);
            }
            return Task.FromResult("nothing ran");
        });
        Infrastructure.MacDebugBridge.Register("sidebar-popup", args =>
        {
            if (!TryDebugRow(args, out _, out var point) || !PrepareContextMenu(point)) return Task.FromResult("no menu");
            var name = args.Length > 1 ? args[1] : "sidebar-popup";
            string snapshot = string.Empty;
            // The menu runs its own tracking loop; a common-mode timer snapshots and dismisses it.
            var timer = NSTimer.CreateTimer(0.8, _ =>
            {
                snapshot = Infrastructure.MacDebugBridge.Snap(name);
                _contextMenu.CancelTracking();
            });
            NSRunLoop.Main.AddTimer(timer, NSRunLoopMode.Common);
            _contextMenu.PopUpMenu(null, point, _outline);
            timer.Invalidate();
            return Task.FromResult($"{ShellSidebarContextMenus.Entries(_contextMenu).Count} entries; {snapshot}");
        });
    }

    private NSMenu? DebugMenu(string[] args)
        => TryDebugRow(args, out _, out var point) && PrepareContextMenu(point) ? _contextMenu : null;

    /// <summary>Parses "ROW" or "ROW.CALENDAR" into a visible row and a point inside it.</summary>
    private bool TryDebugRow(string[] args, out nint row, out CGPoint point)
    {
        row = -1; point = CGPoint.Empty;
        if (args.Length == 0) return false;
        var parts = args[0].Split('.');
        if (!int.TryParse(parts[0], out var index) || index < 0 || index >= _outline.RowCount) return false;
        row = index;
        var rect = _outline.RectForRow(row);
        var y = rect.Y + Math.Min(rect.Height / 2, ShellCalendarGroupCell.HeaderHeight / 2);
        if (parts.Length > 1 && int.TryParse(parts[1], out var calendar))
            y = rect.Y + 2 + ShellCalendarGroupCell.HeaderHeight + (calendar + 0.5) * ShellCalendarGroupCell.CalendarRowHeight;
        point = new CGPoint(rect.X + rect.Width / 2, y);
        return true;
    }

    private static void Describe(NSMenu menu, string prefix, List<string> lines)
    {
        var entries = ShellSidebarContextMenus.Entries(menu);
        for (int index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            lines.Add($"{prefix}{index}:{entry.Title}{(entry.Enabled ? string.Empty : " [disabled]")}");
            if (entry.Submenu is { } submenu) Describe(submenu, $"{prefix}{index}.", lines);
        }
    }
#endif

    #endregion

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _outline.Activated -= Clicked;
            _outline.PrepareMenu = null;
            _outline.DragFinished = null;
            _outline.Menu = null;
            _contextMenu.RemoveAllItems();
            _outline.DataSource = null;
            _outline.Delegate = null;
            _rows.Dispose();
            _selection.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class Node(IMenuItem item) : NSObject
    {
        public IMenuItem Item { get; } = item;
    }

    private sealed class Rows(ShellSidebarViewController owner) : NSOutlineViewDataSource
    {
        private readonly Dictionary<IMenuItem, Node> _nodes = new();
        private readonly List<INotifyCollectionChanged> _collections = new();
        private readonly List<INotifyPropertyChanged> _properties = new();
        private IList<IMenuItem>? _items;
        private bool _disposed;
        private bool _reloadQueued;

        public bool TryGetNode(IMenuItem item, out Node node) => _nodes.TryGetValue(item, out node!);

        public bool HasChildren(IMenuItem item) => ShellPaneRows.Children(item).Any(ShellPaneRows.IsShown);

        public void Bind(IList<IMenuItem>? items)
        {
            Detach();
            _items = items;
            Observe(items);
            owner._outline.ReloadData();
            var live = new HashSet<IMenuItem>(_properties.OfType<IMenuItem>());
            foreach (var stale in _nodes.Keys.Where(key => !live.Contains(key)).ToList())
            {
                _nodes[stale].Dispose();
                _nodes.Remove(stale);
            }
            ExpandFromModel(items);
            owner.ApplySelection();
        }

        private void ExpandFromModel(IEnumerable<IMenuItem>? items)
        {
            foreach (var item in items ?? Array.Empty<IMenuItem>())
            {
                if (!ShellPaneRows.IsShown(item) || !_nodes.TryGetValue(item, out var node)) continue;
                // Accounts start expanded like the Windows pane; folders follow their model state.
                if (item.IsExpanded || item is IAccountMenuItem) owner._outline.ExpandItem(node);
                else owner._outline.CollapseItem(node);
                ExpandFromModel(ShellPaneRows.Children(item));
            }
        }

        private void Observe(IEnumerable<IMenuItem>? items)
        {
            if (items is INotifyCollectionChanged changed) { _collections.Add(changed); changed.CollectionChanged += CollectionChanged; }
            foreach (var item in items ?? Array.Empty<IMenuItem>())
            {
                if (!_nodes.ContainsKey(item)) _nodes.Add(item, new Node(item));
                if (item is INotifyPropertyChanged property) { _properties.Add(property); property.PropertyChanged += PropertyChanged; }
                Observe(ShellPaneRows.Children(item));
            }
        }

        private void Detach()
        {
            foreach (var collection in _collections) collection.CollectionChanged -= CollectionChanged;
            foreach (var property in _properties) property.PropertyChanged -= PropertyChanged;
            _collections.Clear();
            _properties.Clear();
        }

        private async void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            // Menus are rebuilt in bursts; coalesce into one reload per dispatcher turn.
            if (_reloadQueued) return;
            _reloadQueued = true;
            try
            {
                await owner._dispatcher.ExecuteOnUIThread(() =>
                {
                    _reloadQueued = false;
                    if (!_disposed) Bind(_items);
                });
            }
            catch (Exception exception) { if (!_disposed) owner._error(exception); }
        }

        private async void PropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            try
            {
                await owner._dispatcher.ExecuteOnUIThread(() =>
                {
                    if (_disposed || sender is not IMenuItem item || !_nodes.TryGetValue(item, out var node)) return;
                    switch (args.PropertyName)
                    {
                        case nameof(IMenuItem.IsExpanded):
                            if (item.IsExpanded) owner._outline.ExpandItem(node);
                            else owner._outline.CollapseItem(node);
                            Refresh(item, node, remeasure: false);
                            return;
                        case nameof(MenuItemBase.IsEnabled) when item is NewContactMenuItem or NewAddressListMenuItem:
                            Bind(_items);
                            return;
                        case nameof(CalendarDatePickerMenuItem.IsCalendarExpanded):
                            Refresh(item, node, remeasure: true);
                            return;
                        default:
                            Refresh(item, node, remeasure: false);
                            return;
                    }
                });
            }
            catch (Exception exception) { if (!_disposed) owner._error(exception); }
        }

        private void Refresh(IMenuItem item, Node node, bool remeasure)
        {
            var row = owner._outline.RowForItem(node);
            if (row < 0) return;
            if (remeasure) owner._outline.NoteHeightOfRowsWithIndexesChanged(NSIndexSet.FromIndex(row));
            if (owner._outline.GetRowView(row, false) is WinoShellRowView rowView) rowView.ShowsIndicator = ShellPaneRows.ShowsIndicator(item);
            owner._selection.Configure(owner._outline.GetView(0, row, false), item, row);
        }

        public void Remeasure(NSView cell)
        {
            var row = owner._outline.RowForView(cell);
            if (row >= 0) owner._outline.NoteHeightOfRowsWithIndexesChanged(NSIndexSet.FromIndex(row));
        }

        private IList<IMenuItem> Items(NSObject? item)
        {
            var items = item is Node node ? ShellPaneRows.Children(node.Item) : _items ?? (IEnumerable<IMenuItem>)Array.Empty<IMenuItem>();
            return items.Where(ShellPaneRows.IsShown).ToList();
        }

        public override nint GetChildrenCount(NSOutlineView outlineView, NSObject? item) => Items(item).Count;

        public override NSObject GetChild(NSOutlineView outlineView, nint childIndex, NSObject? item)
        {
            var child = Items(item)[(int)childIndex];
            if (!_nodes.TryGetValue(child, out var node)) _nodes[child] = node = new Node(child);
            return node;
        }

        public override bool ItemExpandable(NSOutlineView outlineView, NSObject item) => Items(item).Count > 0;

        public override NSDragOperation ValidateDrop(NSOutlineView outlineView, INSDraggingInfo info, NSObject? item, nint index)
            => owner.ValidateMailDrop(info);

        public override bool AcceptDrop(NSOutlineView outlineView, INSDraggingInfo info, NSObject? item, nint index)
            => owner.AcceptMailDrop(info);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed = true;
                Detach();
                foreach (var node in _nodes.Values) node.Dispose();
                _nodes.Clear();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class Selection(ShellSidebarViewController owner) : NSOutlineViewDelegate
    {
        public override NSView GetView(NSOutlineView outlineView, NSTableColumn? tableColumn, NSObject item)
        {
            var menuItem = ((Node)item).Item;
            var row = outlineView.RowForItem(item);
            NSView view = menuItem switch
            {
                ShellSectionHeaderMenuItem => outlineView.MakeView(ShellSectionHeaderCell.ReuseIdentifier, this) as ShellSectionHeaderCell ?? new ShellSectionHeaderCell(),
                SeperatorItem => outlineView.MakeView(ShellSeparatorCell.ReuseIdentifier, this) as ShellSeparatorCell ?? new ShellSeparatorCell(),
                FixAccountIssuesMenuItem => outlineView.MakeView(ShellFixAccountCell.ReuseIdentifier, this) as ShellFixAccountCell ?? new ShellFixAccountCell(),
                CalendarDatePickerMenuItem => outlineView.MakeView(ShellDatePickerCell.ReuseIdentifier, this) as ShellDatePickerCell ?? new ShellDatePickerCell(owner._context),
                AccountCalendarGroupMenuItem => outlineView.MakeView(ShellCalendarGroupCell.ReuseIdentifier, this) as ShellCalendarGroupCell ?? CreateGroupCell(),
                _ => outlineView.MakeView(ShellPaneCell.ReuseIdentifier, this) as ShellPaneCell ?? CreatePaneCell()
            };
            Configure(view, menuItem, row);
            return view;
        }

        private ShellPaneCell CreatePaneCell()
        {
            var cell = new ShellPaneCell(owner._context);
            cell.AttentionClicked += item => owner._attention(item);
            cell.ActionClicked += item =>
            {
                if (item is NewTaskListMenuItem { NewGroupRequested: { } request }) _ = Observe(request());
            };
            return cell;
        }

        private async Task Observe(Task task)
        {
            try { await task; }
            catch (Exception exception) { owner._error(exception); }
        }

        private ShellCalendarGroupCell CreateGroupCell()
        {
            var cell = new ShellCalendarGroupCell();
            cell.HeightChanged += owner._rows.Remeasure;
            return cell;
        }

        public void Configure(NSView? view, IMenuItem item, nint row)
        {
            switch (view)
            {
                case ShellPaneCell cell:
                    cell.Configure(item, owner._outline.SelectedRow == row && row >= 0, owner._rows.HasChildren(item));
                    break;
                case ShellSectionHeaderCell header when item is ShellSectionHeaderMenuItem section:
                    header.Configure(section.Title);
                    break;
                case ShellFixAccountCell fix when item is FixAccountIssuesMenuItem issue:
                    fix.Configure(issue);
                    break;
                case ShellDatePickerCell picker when item is CalendarDatePickerMenuItem date:
                    picker.Configure(date);
                    break;
                case ShellCalendarGroupCell group when item is AccountCalendarGroupMenuItem calendars:
                    group.Configure(calendars);
                    break;
            }
        }

        public override NSTableRowView RowViewForItem(NSOutlineView outlineView, NSObject item)
        {
            var menuItem = ((Node)item).Item;
            var row = outlineView.MakeView("WinoShellRow", this) as WinoShellRowView ?? new WinoShellRowView { Identifier = "WinoShellRow" };
            row.Highlightable = ShellPaneRows.IsHighlightable(menuItem);
            row.IsDropTarget = false;
            row.ShowsIndicator = ShellPaneRows.ShowsIndicator(menuItem);
            return row;
        }

        public override nfloat GetRowHeight(NSOutlineView outlineView, NSObject item) => (nfloat)ShellPaneRows.Height(((Node)item).Item);

        public override bool ShouldShowOutlineCell(NSOutlineView outlineView, NSObject item) => false;

        public override bool ShouldSelectItem(NSOutlineView outlineView, NSObject item)
        {
            var node = (Node)item;
            if (!owner.IsSelectable(node.Item)) return false;
            // A click on the chevron of an expandable folder toggles it instead of selecting.
            if (owner._rows.HasChildren(node.Item) && NSApplication.SharedApplication.CurrentEvent is { Type: NSEventType.LeftMouseDown } click)
            {
                var row = outlineView.RowForItem(item);
                if (row >= 0 && outlineView.GetView(0, row, false) is ShellPaneCell cell && cell.IsInChevronArea(cell.ConvertPointFromView(click.LocationInWindow, null)))
                {
                    owner.ToggleExpansion(node);
                    owner._selectionHandledRow = row;
                    return false;
                }
            }
            return true;
        }

        public override void SelectionDidChange(NSNotification notification)
        {
            var previous = owner._selectedItem;
            if (previous is not null && owner._rows.TryGetNode(previous, out var previousNode))
            {
                var previousRow = owner._outline.RowForItem(previousNode);
                if (previousRow >= 0) Configure(owner._outline.GetView(0, previousRow, false), previous, previousRow);
            }
            var row = owner._outline.SelectedRow;
            if (row < 0 || owner._outline.ItemAtRow(row) is not Node node) return;
            Configure(owner._outline.GetView(0, row, false), node.Item, row);
            if (owner._applyingSelection) return;
            owner._selectedItem = node.Item;
            owner._selectionHandledRow = row;
            owner.Invoke(node.Item);
        }

        public override void ItemDidExpand(NSNotification notification)
        {
            if (notification.UserInfo?["NSObject"] is Node node && !node.Item.IsExpanded && node.Item is MenuItemBase menu) menu.IsExpanded = true;
        }

        public override void ItemDidCollapse(NSNotification notification)
        {
            if (notification.UserInfo?["NSObject"] is Node node && node.Item.IsExpanded && node.Item is MenuItemBase menu) menu.IsExpanded = false;
        }
    }
}
