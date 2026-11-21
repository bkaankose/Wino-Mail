using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.MenuItems;
using Wino.Mail.ViewModels.MenuItems.Contracts;
using Wino.Mail.ViewModels.Shell;
using Wino.Mail.ViewModels.Shell.Contracts;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Bindings;
using Wino.Presentation.Linux.Lists;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Shell;

/// <summary>A row kind drawn by another package (calendar group, date picker). Returns null for items it does not own.</summary>
internal interface IShellSidebarRowProvider
{
    Gtk.Widget? CreateOrUpdate(IMenuItem item, Gtk.Widget? existing);

    void Release(Gtk.Widget widget);
}

/// <summary>
/// The sidebar: the current mode's shared menu as a flat keyed list (<see cref="ShellMenuRowPresentation.Flatten"/>)
/// in a <see cref="Gtk.ListView"/>. A structure change re-flattens once per main-loop turn and splices only the rows
/// that changed; any other property change repaints its one row. A row is invoked by a selection change (pointer,
/// accessibility clients) or by Enter; the arrow keys move the focus only.
/// </summary>
internal sealed partial class ShellSidebarView : IDisposable
{
    private readonly WinoAppShellViewModel _viewModel;
    private readonly IPictureStorageService? _pictures;
    private readonly Action<Exception> _error;
    private readonly BindingScope _bindings;
    private readonly ObservableListModel<IMenuItem> _model;
    private readonly WinoCellFactory<ShellSidebarCell, IMenuItem> _factory;
    private readonly Gtk.SingleSelection _selection;
    private readonly Adw.WindowTitle _title = Adw.WindowTitle.New(AppActions.ApplicationName, string.Empty);
    private readonly Adw.Bin _footerHost = Adw.Bin.New();
    private readonly List<IMenuItem> _rows = [];
    private Dictionary<IMenuItem, int> _depths = new(ReferenceEqualityComparer.Instance);
    private readonly List<IDisposable> _watches = [];
    private readonly List<IShellSidebarRowProvider> _providers = [];
    private readonly MainLoop.Coalescer _reflatten;
    private readonly MainLoop.Throttle _typeAheadReset;
    private readonly EventHandler<string> _preferenceChanged;
    private ShellMenu? _menu;
    private IMenuItem? _selected;
    private WinoApplicationMode _mode = WinoApplicationMode.Mail;
    private IMenuItem? _invokedBySelection;
    private string _typeAhead = string.Empty;
    private bool _applyingSelection;
    private bool _keyboardNavigating;
    private bool _disposed;

    public ShellSidebarView(WinoAppShellViewModel viewModel, GtkBinder<WinoAppShellViewModel> bind, IPictureStorageService? pictures, Action<Exception> error)
    {
        _viewModel = viewModel;
        _pictures = pictures;
        _error = error;
        WinoLiveObjects.Track(this);
        var child = bind.Child();
        _bindings = child.Scope;
        _reflatten = new MainLoop.Coalescer(Reflatten);
        _typeAheadReset = new MainLoop.Throttle(1000, () => _typeAhead = string.Empty);
        _model = new ObservableListModel<IMenuItem>(new InlineDispatcher(), error);
        _factory = new WinoCellFactory<ShellSidebarCell, IMenuItem>(CreateCell, error)
        {
            IsSelectable = IsSelectable,
            Setup = (cell, listItem) => CellSetup?.Invoke(cell, listItem),
            Teardown = cell =>
            {
                CellTeardown?.Invoke(cell);
                cell.Dispose();
            }
        };
        _selection = Gtk.SingleSelection.New(_model.Store);
        _selection.SetAutoselect(false);
        _selection.SetCanUnselect(true);

        List = Gtk.ListView.New(_selection, _factory.Factory);
        List.AddCssClass("navigation-sidebar");
        List.AddCssClass(WinoCssClass.SidebarList);
        List.SetSingleClickActivate(false);
        List.SetTabBehavior(Gtk.ListTabBehavior.Item);
        WinoA11y.Label(List, ShellModeActivator.ModeTitle(_mode));

        var list = List;
        var selection = _selection;
        _bindings.Own(GtkSignals.OnNotify(selection, "selected", OnSelectionChanged));
        _bindings.Own(GtkSignals.Connect<Gtk.ListView, Gtk.ListView.ActivateSignalArgs>(
            h => list.OnActivate += h, h => list.OnActivate -= h, (_, args) => OnActivate(args.Position)));
        var keys = Gtk.EventControllerKey.New();
        keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        _bindings.Own(GtkSignals.Connect<Gtk.EventControllerKey, Gtk.EventControllerKey.KeyPressedSignalArgs, bool>(
            h => keys.OnKeyPressed += h, h => keys.OnKeyPressed -= h, OnKeyPressed));
        list.AddController(keys);
        _bindings.Own(new ActionDisposable(() => list.RemoveController(keys)));

        _preferenceChanged = OnPreferenceChanged;
        viewModel.PreferencesService.PreferenceChanged += _preferenceChanged;

        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.SetVexpand(true);
        scroll.SetChild(List);
        Scroller = scroll;
        var header = Adw.HeaderBar.New();
        header.SetTitleWidget(_title);
        _footerHost.SetVisible(false);
        var root = Adw.ToolbarView.New();
        root.AddTopBar(header);
        root.SetContent(scroll);
        root.AddBottomBar(_footerHost);
        Root = root;

        child.Bind(nameof(WinoAppShellViewModel.CurrentMenu), static vm => vm.CurrentMenu, SetMenu);
        child.Bind(nameof(WinoAppShellViewModel.SelectedMenuItem), static vm => vm.SelectedMenuItem, item => SetSelectedItem(item as IMenuItem));
        child.Bind(nameof(WinoAppShellViewModel.CurrentMode), static vm => vm.CurrentMode, SetMode);
    }

    public Gtk.Widget Root { get; }

    public Gtk.ListView List { get; }

    public Gtk.ScrolledWindow Scroller { get; }

    public IReadOnlyList<IMenuItem> Rows => _rows;

    /// <summary>Raised after a row that navigates was invoked (the shell closes the overlay sidebar).</summary>
    public event Action? ItemInvoked;

    /// <summary>Raised after the rows changed (a re-flatten that spliced the model).</summary>
    public event Action? RowsChanged;

    /// <summary>Attach controllers once per cell (drop targets, context menus). Set before the first menu arrives.</summary>
    public Action<ShellSidebarCell, Gtk.ListItem>? CellSetup { get; set; }

    public Action<ShellSidebarCell>? CellTeardown { get; set; }

    public void SetTitle(string title) => _title.SetTitle(title);

    public void SetFooter(Gtk.Widget? footer)
    {
        _footerHost.SetChild(footer);
        _footerHost.SetVisible(footer is not null);
    }

    public void AddRowProvider(IShellSidebarRowProvider provider)
    {
        _providers.Add(provider);
        Reload();
    }

    public void SetMenu(ShellMenu? menu)
    {
        if (_disposed) return;
        _menu = menu;
        Reflatten();
    }

    public void SetSelectedItem(IMenuItem? item)
    {
        if (_disposed) return;
        var previous = _selected;
        _selected = item;
        var expanded = false;
        for (var parent = item?.ParentMenuItem; parent is not null; parent = parent.ParentMenuItem)
        {
            if (parent is MergedAccountMenuItem or AccountGroupMenuItem || parent.IsExpanded) continue;
            parent.IsExpanded = true;
            expanded = true;
        }
        if (expanded) Reflatten();
        ApplySelection();
        if (previous is not null) RefreshRow(previous);
        if (item is not null)
        {
            RefreshRow(item);
            var index = IndexOf(item);
            if (index >= 0 && _factory.CellOf(item) is null) ListViews.ScrollTo(List, (uint)index);
        }
    }

    public void SetMode(WinoApplicationMode mode)
    {
        if (_disposed) return;
        _mode = mode;
        WinoA11y.Label(List, ShellModeActivator.ModeTitle(mode));
        Reload();
    }

    /// <summary>Re-describes every bound row, after a preference or a language change.</summary>
    public void Reload()
    {
        if (_disposed) return;
        _factory.ForEachBound((cell, item) => cell.Show(Describe(item)));
    }

    public bool FocusList() => List.GrabFocus();

    public IMenuItem? ItemAt(Gtk.Widget cellRoot)
    {
        for (Gtk.Widget? widget = cellRoot; widget is not null; widget = widget.GetParent())
        {
            if (widget is Adw.Bin bin && _cells.TryGetValue(bin, out var cell)) return _factory.ItemOf(cell);
        }
        return null;
    }

    public ShellSidebarCell? CellOf(IMenuItem item) => _factory.CellOf(item);

    public int IndexOf(IMenuItem item) => _model.IndexOf(item);

    /// <summary>The row the keyboard focus is on, or null.</summary>
    public IMenuItem? FocusedItem()
    {
        IMenuItem? focused = null;
        _factory.ForEachBound((cell, item) =>
        {
            if (cell.Root.GetParent() is { HasFocus: true }) focused = item;
        });
        return focused;
    }

    public async Task InvokeAsync(IMenuItem item)
    {
        if (_disposed) return;
        if (!ShellMenuItemRules.IsInteractive(item) || !ShellMenuItemRules.IsEnabled(item))
        {
            ApplySelection();
            return;
        }
        var navigated = false;
        try
        {
            if (item is FixAccountIssuesMenuItem fix)
            {
                await FixAccountAsync(fix.Account, item);
                return;
            }
            // Parents that only group their children (More) expand in place, as in the Android drawer. The rule is
            // repeated here because shared code has no such rule yet; it belongs in ShellMenuItemRules.
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
        catch (Exception exception) { _error(exception); }
        finally
        {
            if (!_disposed)
            {
                ApplySelection();
                if (navigated) ItemInvoked?.Invoke();
            }
        }
    }

    public void ToggleExpansion(IMenuItem item)
    {
        if (!ShellMenuRowPresentation.HasChildren(item) || item is IAccountMenuItem) return;
        item.IsExpanded = !item.IsExpanded;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearWatches();
        _viewModel.PreferencesService.PreferenceChanged -= _preferenceChanged;
        _reflatten.Dispose();
        _typeAheadReset.Dispose();
        _bindings.Dispose();
        List.SetModel(null);
        List.SetFactory(null);
        _factory.Dispose();
        _model.Dispose();
        _cells.Clear();
        ItemInvoked = null;
        RowsChanged = null;
        CellSetup = null;
        CellTeardown = null;
    }

    private readonly Dictionary<Adw.Bin, ShellSidebarCell> _cells = new(ReferenceEqualityComparer.Instance);

    private ShellSidebarCell CreateCell()
    {
        var cell = new ShellSidebarCell(this);
        _cells[cell.Bin] = cell;
        return cell;
    }

    internal void Forget(ShellSidebarCell cell) => _cells.Remove(cell.Bin);

    private void Reflatten()
    {
        if (_disposed) return;
        ClearWatches();
        var flat = ShellMenuRowPresentation.Flatten(_menu?.Items, includeNewItems: true);
        var previousDepths = _depths;
        var depths = new Dictionary<IMenuItem, int>(ReferenceEqualityComparer.Instance);
        _rows.Clear();
        foreach (var (item, depth) in flat)
        {
            if (item.ParentMenuItem is AccountTaskListAccountMenuItem) continue;
            if (!depths.TryAdd(item, depth)) continue;
            _rows.Add(item);
        }
        _depths = depths;

        if (_menu?.Items is INotifyCollectionChanged root) WatchCollection(root);
        foreach (var item in _rows)
        {
            WatchItem(item);
            if (ShellMenuItemRules.Children(item) is INotifyCollectionChanged children) WatchCollection(children);
        }

        Serilog.Log.Debug("Sidebar re-flattened: {Count} rows.", _rows.Count);
        var changed = false;
        Action<ListChange> onChanged = _ => changed = true;
        _model.Changed += onChanged;
        try { _model.SetItems([.. _rows]); }
        finally { _model.Changed -= onChanged; }
        ApplySelection();

        foreach (var item in _rows)
        {
            if (!previousDepths.TryGetValue(item, out var before) || before != depths[item]
                || ShellMenuRowPresentation.HasChildren(item) || item.ParentMenuItem is null) RefreshRow(item);
        }
        if (changed) RowsChanged?.Invoke();
    }

    private void WatchCollection(INotifyCollectionChanged collection)
    {
        NotifyCollectionChangedEventHandler handler = (_, _) => MainLoop.Post(() =>
        {
            if (!_disposed) _reflatten.Request();
        });
        collection.CollectionChanged += handler;
        _watches.Add(new ActionDisposable(() => collection.CollectionChanged -= handler));
    }

    private void WatchItem(IMenuItem item)
    {
        if (item is not INotifyPropertyChanged source) return;
        PropertyChangedEventHandler handler = (_, args) =>
        {
            var property = args.PropertyName;
            MainLoop.Post(() => OnItemChanged(item, property));
        };
        source.PropertyChanged += handler;
        _watches.Add(new ActionDisposable(() => source.PropertyChanged -= handler));
    }

    private void ClearWatches()
    {
        foreach (var watch in _watches) watch.Dispose();
        _watches.Clear();
    }

    private void OnItemChanged(IMenuItem item, string? property)
    {
        if (_disposed) return;
        switch (property)
        {
            case nameof(IMenuItem.IsExpanded):
                _reflatten.Request();
                RefreshRow(item);
                break;
            case nameof(MenuItemBase.IsEnabled) when item is NewContactMenuItem or NewAddressListMenuItem or NewAddressBookMenuItem:
                _reflatten.Request();
                break;
            default:
                RefreshRow(item);
                break;
        }
    }

    private void RefreshRow(IMenuItem item)
    {
        if (_factory.CellOf(item) is { } cell && _depths.ContainsKey(item)) cell.Show(Describe(item));
    }

    private void OnPreferenceChanged(object? sender, string name)
    {
        if (name != nameof(IPreferencesService.IsCompactAccountMenuItemEnabled)) return;
        MainLoop.Post(Reload);
    }

    private bool IsSelectable(IMenuItem item)
        => _menu is not null && ShellMenuItemRules.IsInteractive(item) && ShellMenuItemRules.IsEnabled(item);

    private void ApplySelection()
    {
        if (_disposed) return;
        _applyingSelection = true;
        try
        {
            var index = _selected is null || _menu is not { HandlesSelection: true } ? -1 : IndexOf(_selected);
            if (index < 0) _selection.UnselectAll();
            else if (_selection.GetSelected() != (uint)index) _selection.SetSelected((uint)index);
        }
        finally { _applyingSelection = false; }
    }

    private void OnSelectionChanged()
    {
        if (_disposed || _applyingSelection || _model.IsApplying) return;
        var item = ListViews.SelectedValue<IMenuItem>(_selection);
        if (item is null) return;
        // The arrow keys move the selection with the focus in a GTK list; here they only move the focus (a GNOME
        // sidebar activates on Enter), so a keyboard move puts the ViewModel's selection back on the next turn.
        if (_keyboardNavigating)
        {
            MainLoop.Post(ApplySelection);
            return;
        }
        _invokedBySelection = item;
        MainLoop.Post(() => _invokedBySelection = null);
        Observe(InvokeAsync(item));
    }

    private void OnActivate(uint position)
    {
        if (_disposed || position >= _rows.Count) return;
        var item = _model[(int)position];
        if (ReferenceEquals(item, _invokedBySelection)) return;
        Observe(InvokeAsync(item));
    }

    private bool OnKeyPressed(Gtk.EventControllerKey sender, Gtk.EventControllerKey.KeyPressedSignalArgs args)
    {
        var key = args.Keyval;
        switch (key)
        {
            case Gdk.Constants.KEY_Up or Gdk.Constants.KEY_Down or Gdk.Constants.KEY_Home or Gdk.Constants.KEY_End
                or Gdk.Constants.KEY_Page_Up or Gdk.Constants.KEY_Page_Down or Gdk.Constants.KEY_KP_Up or Gdk.Constants.KEY_KP_Down
                or Gdk.Constants.KEY_KP_Home or Gdk.Constants.KEY_KP_End or Gdk.Constants.KEY_KP_Page_Up or Gdk.Constants.KEY_KP_Page_Down:
                _keyboardNavigating = true;
                MainLoop.Post(() => _keyboardNavigating = false);
                return false;
            case Gdk.Constants.KEY_Left or Gdk.Constants.KEY_Right or Gdk.Constants.KEY_KP_Left or Gdk.Constants.KEY_KP_Right:
                var right = key is Gdk.Constants.KEY_Right or Gdk.Constants.KEY_KP_Right;
                if (List.GetDirection() == Gtk.TextDirection.Rtl) right = !right;
                return right ? ExpandFocused() : CollapseFocused();
        }
        if ((args.State & (Gdk.ModifierType.ControlMask | Gdk.ModifierType.AltMask)) != 0) return false;
        var character = Gdk.Functions.KeyvalToUnicode(key);
        if (character < 0x20 || character == ' ' || char.IsControl((char)character)) return false;
        return TypeAhead(char.ConvertFromUtf32((int)character));
    }

    private bool ExpandFocused()
    {
        if (FocusedItem() is not { } item || !ShellMenuRowPresentation.HasChildren(item) || item is IAccountMenuItem || item.IsExpanded) return false;
        item.IsExpanded = true;
        return true;
    }

    private bool CollapseFocused()
    {
        if (FocusedItem() is not { } item) return false;
        if (ShellMenuRowPresentation.HasChildren(item) && item is not IAccountMenuItem && item.IsExpanded)
        {
            item.IsExpanded = false;
            return true;
        }
        if (item.ParentMenuItem is { } parent && IndexOf(parent) is >= 0 and var index)
        {
            FocusRow((uint)index);
            return true;
        }
        return false;
    }

    private bool TypeAhead(string text)
    {
        _typeAhead += text;
        _typeAheadReset.Request();
        var start = FocusedItem() is { } focused ? IndexOf(focused) : -1;
        for (var step = 1; step <= _rows.Count; step++)
        {
            var index = (start + step + _rows.Count) % _rows.Count;
            var item = _model[index];
            if (ShellMenuRowPresentation.KindOf(item) == ShellMenuRowKind.Header) continue;
            var title = _depths.TryGetValue(item, out var depth) ? ShellMenuRowPresentation.Describe(item, depth, null).Title : string.Empty;
            if (!title.StartsWith(_typeAhead, true, CultureInfo.CurrentCulture)) continue;
            FocusRow((uint)index);
            return true;
        }
        return false;
    }

    private void FocusRow(uint index)
    {
        _keyboardNavigating = true;
        List.ScrollTo(index, Gtk.ListScrollFlags.Focus, null);
        MainLoop.Post(() => _keyboardNavigating = false);
    }

    private async Task FixAccountAsync(MailAccount? account, IMenuItem fallback)
    {
        if (account is not null && _viewModel.GetProvider(WinoApplicationMode.Mail) is IMailShellClient mail) await mail.HandleAccountAttentionAsync(account);
        else await _viewModel.InvokeMenuItemAsync(fallback);
    }

    internal void AttentionClicked(IMenuItem item)
    {
        if (item is IAccountNavigationMenuItem account) Observe(FixAccountAsync(account.Account, item));
    }

    internal void ActionClicked(IMenuItem item)
    {
        if (item is NewTaskListMenuItem { NewGroupRequested: { } request }) Observe(request());
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { _error(exception); }
    }

    internal ShellSidebarRow Describe(IMenuItem item)
    {
        var depth = _depths.TryGetValue(item, out var known) ? known : 0;
        foreach (var provider in _providers)
        {
            if (provider.CreateOrUpdate(item, null) is { } widget) return new ShellSidebarRow(item, null, null, widget, provider);
        }
        if (item is Wino.Mail.ViewModels.Calendar.Data.AccountCalendarGroupMenuItem or CalendarDatePickerMenuItem)
            return new ShellSidebarRow(item, null, null, null, null);

        var content = ShellMenuRowPresentation.Describe(item, depth, _pictures);
        var selected = _menu is { HandlesSelection: true } && ReferenceEquals(item, _selected);
        var weight = selected ? WinoShellRowTitleWeight.Semibold : content.ShowsIndicator ? WinoShellRowTitleWeight.Medium : WinoShellRowTitleWeight.Regular;
        var accent = false;
        var extras = new WinoShellRowExtras(IsSelected: selected);
        switch (item)
        {
            case MergedAccountMenuItem merged:
                var active = selected || merged.IsSelected || merged.IsChildSelected;
                extras = extras with
                {
                    AccountStack = [.. merged.HoldingAccounts.Take(3).Select(account => _pictures is null
                        ? MailAccountIconInfoFactory.CreateProviderFallback(account.ProviderType, account.SpecialImapProvider)
                        : MailAccountIconInfoFactory.Create(account, _pictures))],
                };
                if (active) weight = _mode == WinoApplicationMode.Mail ? WinoShellRowTitleWeight.Semibold : WinoShellRowTitleWeight.Medium;
                break;
            case AccountGroupMenuItem group:
                extras = extras with { AccessibleName = group.AutomationName, Tooltip = group.Name };
                weight = WinoShellRowTitleWeight.Semibold;
                break;
            case IAccountNavigationMenuItem account when ShellMenuItemRules.IsAccountRow(account):
                extras = extras with
                {
                    CompactAccount = _viewModel.PreferencesService.IsCompactAccountMenuItemEnabled,
                    UsesAccountIcon = true,
                    Tooltip = account.AccountAddress
                };
                if (selected || account.IsSelected)
                {
                    accent = _mode == WinoApplicationMode.Mail;
                    weight = accent ? WinoShellRowTitleWeight.Semibold : WinoShellRowTitleWeight.Medium;
                }
                break;
            case FixAccountIssuesMenuItem:
                extras = extras with { IsAttentionRow = true };
                break;
            case MailCategoryMenuItem category:
                extras = extras with { GlyphTintHex = category.BackgroundColorHex ?? category.TextColorHex };
                break;
            case MergedMailCategoryMenuItem mergedCategory:
                extras = extras with { GlyphTintHex = mergedCategory.TextColorHex, TitleColorHex = mergedCategory.TextColorHex };
                break;
            case FolderMenuItem { HasTextColor: true } coloured:
                extras = extras with { TitleColorHex = coloured.Parameter?.TextColorHex, TitleChipHex = coloured.Parameter?.BackgroundColorHex };
                break;
            case ContactFilterViewModel { IsCategory: true } filter:
                extras = extras with { GlyphTintHex = filter.CategoryColorHex };
                break;
            case AccountTaskListMenuItem taskList:
                extras = extras with { GlyphTintHex = taskList.ColorHex };
                break;
            case NewTaskListMenuItem:
                extras = extras with { ShowsAction = true };
                break;
        }
        return new ShellSidebarRow(item, content, extras with { Weight = weight, AccentTitle = accent }, null, null);
    }

    private sealed class InlineDispatcher : IDispatcher
    {
        public Task ExecuteOnUIThread(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }
}

/// <summary>What one sidebar cell shows: a shared row, a provider's widget, or nothing (a zero-height row).</summary>
internal sealed record ShellSidebarRow(IMenuItem Item, ShellMenuRowContent? Content, WinoShellRowExtras? Extras, Gtk.Widget? Custom, IShellSidebarRowProvider? Provider);

internal sealed class ShellSidebarCell : IWinoCell<IMenuItem>, IDisposable
{
    private readonly ShellSidebarView _owner;
    private readonly IDisposable[] _signals;
    private Gtk.ListItem? _listItem;
    private ShellSidebarRow? _shown;

    public ShellSidebarCell(ShellSidebarView owner)
    {
        _owner = owner;
        Row = new WinoShellRow(Translator.Buttons_FixAccount, Translator.ToDoPage_NewGroup);
        Bin.SetChild(Row.Root);
        var row = Row;
        _signals =
        [
            Subscribe(h => row.ChevronClicked += h, h => row.ChevronClicked -= h, () => { if (Item is { } item) _owner.ToggleExpansion(item); }),
            Subscribe(h => row.AttentionClicked += h, h => row.AttentionClicked -= h, () => { if (Item is { } item) _owner.AttentionClicked(item); }),
            Subscribe(h => row.ActionClicked += h, h => row.ActionClicked -= h, () => { if (Item is { } item) _owner.ActionClicked(item); }),
        ];
    }

    internal Adw.Bin Bin { get; } = Adw.Bin.New();

    public Gtk.Widget Root => Bin;

    public WinoShellRow Row { get; }

    public IMenuItem? Item { get; private set; }

    public void Bind(IMenuItem item, WinoCellBinding binding)
    {
        Item = item;
        _listItem = binding.ListItem;
        Show(_owner.Describe(item));
    }

    public void Unbind()
    {
        ReleaseCustom();
        Item = null;
        _listItem = null;
    }

    internal void Show(ShellSidebarRow shown)
    {
        if (!ReferenceEquals(shown.Item, Item)) return;
        if (_shown?.Custom is { } previous && !ReferenceEquals(previous, shown.Custom)) ReleaseCustom();
        _shown = shown;
        var plain = true;
        string name;
        if (shown.Custom is { } custom)
        {
            Bin.SetChild(custom);
            name = string.Empty;
        }
        else if (shown.Content is { } content && shown.Extras is { } extras)
        {
            if (!ReferenceEquals(Bin.GetChild(), Row.Root)) Bin.SetChild(Row.Root);
            Row.Root.SetVisible(true);
            Row.Apply(content, extras);
            name = content.IsExpanded is { } expanded
                ? $"{Row.AccessibleName}, {(expanded ? Translator.Accessibility_MailThreadExpanded : Translator.Accessibility_MailThreadCollapsed)}"
                : Row.AccessibleName;
            plain = content.Kind == ShellMenuRowKind.Header;
        }
        else
        {
            if (!ReferenceEquals(Bin.GetChild(), Row.Root)) Bin.SetChild(Row.Root);
            Row.Root.SetVisible(false);
            name = string.Empty;
        }
        if (_listItem is { } listItem)
        {
            listItem.SetAccessibleLabel(name);
            var interactive = ShellMenuItemRules.IsInteractive(shown.Item);
            listItem.SetActivatable(interactive);
            listItem.SetFocusable(interactive);
        }
        if (Bin.GetParent() is { } parent)
        {
            if (plain) parent.AddCssClass(WinoCssClass.RowPlain);
            else parent.RemoveCssClass(WinoCssClass.RowPlain);
        }
    }

    public void Dispose()
    {
        ReleaseCustom();
        foreach (var signal in _signals) signal.Dispose();
        Row.Dispose();
        _owner.Forget(this);
    }

    private void ReleaseCustom()
    {
        if (_shown is { Custom: { } custom, Provider: { } provider })
        {
            if (ReferenceEquals(Bin.GetChild(), custom)) Bin.SetChild(Row.Root);
            provider.Release(custom);
        }
        _shown = null;
    }

    private static IDisposable Subscribe(Action<Action> add, Action<Action> remove, Action handler)
    {
        add(handler);
        return new ActionDisposable(() => remove(handler));
    }
}
