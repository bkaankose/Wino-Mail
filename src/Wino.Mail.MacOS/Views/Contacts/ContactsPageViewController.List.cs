using System.Collections.Specialized;
using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Contacts;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Contacts;

public sealed partial class ContactsPageViewController
{
    /// <summary>A table row: a letter header or a contact.</summary>
    private sealed record ListEntry(AccountContactViewModel? Contact, string Letter);

    private readonly List<ListEntry> _entries = new();
    private readonly List<ContactGroup> _observedGroups = new();
    private NSSearchField _search = null!;
    private NSTableView _table = null!;
    private NSScrollView _scroll = null!;
    private NSView _emptyPanel = null!;
    private NSProgressIndicator _loading = null!;
    private NSProgressIndicator _loadingMore = null!;
    private NSMenu _contextMenu = null!;
    private NSObject? _boundsObserver;
    private IReadOnlyList<AccountContactViewModel>? _searchResults;
    private CancellationTokenSource? _searchCancellation;
    private bool _reloadScheduled;
    private bool _restoringSelection;
    private bool _listBound;

    private NSView BuildListPane()
    {
        var pane = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };

        _search = new NSSearchField { PlaceholderString = Translator.ContactsPage_SearchPlaceholder, TranslatesAutoresizingMaskIntoConstraints = false, SendsSearchStringImmediately = true };
        _search.Changed += (_, _) => Observe(RunSearchAsync(_search.StringValue, select: false));
        WinoAccessibility.Label(_search, Translator.ContactsPage_SearchPlaceholder);
        WinoLayout.Size(_search, -1, 30);

        _table = new NSTableView
        {
            HeaderView = null,
            Style = NSTableViewStyle.Plain,
            IntercellSpacing = new CGSize(0, 2),
            AllowsMultipleSelection = true,
            AllowsEmptySelection = true,
            AllowsTypeSelect = false,
            FloatsGroupRows = false,
            BackgroundColor = NSColor.Clear,
            SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.Regular,
            ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.FirstColumnOnly,
            UsesAutomaticRowHeights = false
        };
        _table.AddColumn(new NSTableColumn("contact") { ResizingMask = NSTableColumnResizing.Autoresizing, Width = 320 });
        _table.DataSource = new ContactsTableDataSource(this);
        _table.Delegate = new ContactsTableDelegate(this);
        _contextMenu = new NSMenu { AutoEnablesItems = false, Delegate = new ContactsMenuDelegate(this) };
        _table.Menu = _contextMenu;
        WinoAccessibility.Label(_table, Translator.ContactsPage_Title);

        _scroll = new NSScrollView { DocumentView = _table, HasVerticalScroller = true, AutohidesScrollers = true, DrawsBackground = false, TranslatesAutoresizingMaskIntoConstraints = false };
        _scroll.ContentView.PostsBoundsChangedNotifications = true;

        _loading = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        _loadingMore = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };

        var emptyIcon = new WinoIconView(WinoIconGlyph.People, 72, WinoStyle.TertiaryText);
        var emptyText = WinoStyle.Label(Translator.ContactsPage_NoContacts, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        emptyText.Alignment = NSTextAlignment.Center;
        var addFirst = CommandButton(Translator.ContactsPage_AddFirstContact, ViewModel.AddContactCommand);
        addFirst.KeyEquivalent = string.Empty;
        addFirst.BezelColor = WinoStyle.Accent;
        var emptyStack = WinoLayout.VStack(10, emptyIcon, emptyText, addFirst);
        emptyStack.Alignment = NSLayoutAttribute.CenterX;
        _emptyPanel = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        _emptyPanel.AddSubview(emptyStack);

        pane.AddSubview(_search);
        pane.AddSubview(_scroll);
        pane.AddSubview(_loadingMore);
        pane.AddSubview(_emptyPanel);
        pane.AddSubview(_loading);
        NSLayoutConstraint.ActivateConstraints(
        [
            _search.TopAnchor.ConstraintEqualTo(pane.TopAnchor, 16),
            _search.LeadingAnchor.ConstraintEqualTo(pane.LeadingAnchor, 16),
            _search.TrailingAnchor.ConstraintEqualTo(pane.TrailingAnchor, -16),
            _scroll.TopAnchor.ConstraintEqualTo(_search.BottomAnchor, 8),
            _scroll.LeadingAnchor.ConstraintEqualTo(pane.LeadingAnchor, 12),
            _scroll.TrailingAnchor.ConstraintEqualTo(pane.TrailingAnchor, -12),
            _scroll.BottomAnchor.ConstraintEqualTo(_loadingMore.TopAnchor, -4),
            _loadingMore.CenterXAnchor.ConstraintEqualTo(pane.CenterXAnchor),
            _loadingMore.BottomAnchor.ConstraintEqualTo(pane.BottomAnchor, -8),
            _loadingMore.HeightAnchor.ConstraintEqualTo(16),
            _emptyPanel.TopAnchor.ConstraintEqualTo(_scroll.TopAnchor),
            _emptyPanel.BottomAnchor.ConstraintEqualTo(_scroll.BottomAnchor),
            _emptyPanel.LeadingAnchor.ConstraintEqualTo(_scroll.LeadingAnchor),
            _emptyPanel.TrailingAnchor.ConstraintEqualTo(_scroll.TrailingAnchor),
            emptyStack.CenterXAnchor.ConstraintEqualTo(_emptyPanel.CenterXAnchor),
            emptyStack.CenterYAnchor.ConstraintEqualTo(_emptyPanel.CenterYAnchor),
            emptyStack.WidthAnchor.ConstraintLessThanOrEqualTo(_emptyPanel.WidthAnchor, 1, -32),
            _loading.CenterXAnchor.ConstraintEqualTo(_scroll.CenterXAnchor),
            _loading.CenterYAnchor.ConstraintEqualTo(_scroll.CenterYAnchor)
        ]);
        return pane;
    }

    private void BindList()
    {
        if (_listBound) return;
        _listBound = true;
        ViewModel.ContactGroups.CollectionChanged += GroupsChanged;
        ViewModel.Contacts.CollectionChanged += ContactsChanged;
        ObserveGroups();
        Bind(nameof(ViewModel.IsLoading), vm => vm.IsLoading, loading =>
        {
            _loading.Hidden = !loading;
            if (loading) _loading.StartAnimation(null); else _loading.StopAnimation(null);
            UpdateEmptyState();
        });
        Bind(nameof(ViewModel.IsLoadingMore), vm => vm.IsLoadingMore, more =>
        {
            _loadingMore.Hidden = !more;
            if (more) _loadingMore.StartAnimation(null); else _loadingMore.StopAnimation(null);
        });
        Bind(nameof(ViewModel.SelectedContact), vm => vm.SelectedContact, contact => SelectRow(contact));
        Bind(nameof(ViewModel.IsSelectionMode), vm => vm.IsSelectionMode, selectionMode =>
        {
            if (!selectionMode && _table.SelectedRowCount > 1) _table.DeselectAll(null);
        });
        _boundsObserver = NSNotificationCenter.DefaultCenter.AddObserver(NSView.BoundsChangedNotification, _ => MaybeLoadMore(), _scroll.ContentView);
        ScheduleReload();
    }

    private void ReleaseList()
    {
        if (!_listBound) return;
        _listBound = false;
        ViewModel.ContactGroups.CollectionChanged -= GroupsChanged;
        ViewModel.Contacts.CollectionChanged -= ContactsChanged;
        foreach (var group in _observedGroups) group.CollectionChanged -= GroupChanged;
        _observedGroups.Clear();
        _searchCancellation?.Cancel();
        if (_boundsObserver is not null) { NSNotificationCenter.DefaultCenter.RemoveObserver(_boundsObserver); _boundsObserver.Dispose(); _boundsObserver = null; }
    }

    private void GroupsChanged(object? sender, NotifyCollectionChangedEventArgs args) => OnUI(() => { ObserveGroups(); ScheduleReload(); });
    private void GroupChanged(object? sender, NotifyCollectionChangedEventArgs args) => OnUI(ScheduleReload);
    private void ContactsChanged(object? sender, NotifyCollectionChangedEventArgs args) => OnUI(UpdateEmptyState);

    private void ObserveGroups()
    {
        foreach (var group in _observedGroups) group.CollectionChanged -= GroupChanged;
        _observedGroups.Clear();
        foreach (var group in ViewModel.ContactGroups)
        {
            group.CollectionChanged += GroupChanged;
            _observedGroups.Add(group);
        }
    }

    /// <summary>Coalesces burst changes into one table reload on the next run-loop turn.</summary>
    private void ScheduleReload()
    {
        if (_reloadScheduled || _released) return;
        _reloadScheduled = true;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            _reloadScheduled = false;
            if (_released) return;
            RebuildEntries();
        });
    }

    private void RebuildEntries()
    {
        _entries.Clear();
        if (_searchResults is { } results)
        {
            string? letter = null;
            foreach (var contact in results)
            {
                if (contact.InitialLetter != letter)
                {
                    letter = contact.InitialLetter;
                    _entries.Add(new ListEntry(null, letter));
                }
                _entries.Add(new ListEntry(contact, letter));
            }
        }
        else
        {
            foreach (var group in ViewModel.ContactGroups)
            {
                if (group.Count == 0) continue;
                _entries.Add(new ListEntry(null, group.Key));
                foreach (var contact in group) _entries.Add(new ListEntry(contact, group.Key));
            }
        }
        _restoringSelection = true;
        try
        {
            _table.ReloadData();
            SelectRow(ViewModel.SelectedContact, scroll: false);
        }
        finally { _restoringSelection = false; }
        UpdateEmptyState();
        MaybeLoadMore();
    }

    private void UpdateEmptyState()
    {
        bool empty = _searchResults is null ? ViewModel.IsEmpty : _searchResults.Count == 0;
        _emptyPanel.Hidden = !empty || ViewModel.IsLoading;
        _scroll.Hidden = empty && !ViewModel.IsLoading;
    }

    private int RowOf(AccountContactViewModel? contact)
    {
        if (contact is null) return -1;
        for (int index = 0; index < _entries.Count; index++)
            if (_entries[index].Contact is { } item && item.Id == contact.Id) return index;
        return -1;
    }

    private void SelectRow(AccountContactViewModel? contact, bool scroll = true)
    {
        int row = RowOf(contact);
        if (row < 0)
        {
            if (contact is null && _table.SelectedRowCount > 0 && !ViewModel.IsSelectionMode)
            {
                _restoringSelection = true;
                try { _table.DeselectAll(null); } finally { _restoringSelection = false; }
            }
            return;
        }
        if (_table.IsRowSelected(row)) return;
        bool previous = _restoringSelection;
        _restoringSelection = true;
        try
        {
            _table.SelectRows(NSIndexSet.FromIndex(row), byExtendingSelection: ViewModel.IsSelectionMode);
            if (scroll) _table.ScrollRowToVisible(row);
        }
        finally { _restoringSelection = previous; }
    }

    /// <summary>Mirrors the table selection into the ViewModel (Windows SelectionChanged + SelectedItem).</summary>
    private void SelectionChanged()
    {
        if (_restoringSelection) return;
        var selected = new List<AccountContactViewModel>();
        foreach (var index in _table.SelectedRows.ToArray())
            if (index < (nuint)_entries.Count && _entries[(int)index].Contact is { } contact) selected.Add(contact);
        var ids = selected.Select(item => item.Id).ToHashSet();
        for (int index = ViewModel.SelectedContacts.Count - 1; index >= 0; index--)
            if (!ids.Contains(ViewModel.SelectedContacts[index].Id)) ViewModel.SelectedContacts.RemoveAt(index);
        foreach (var contact in selected)
            if (ViewModel.SelectedContacts.All(item => item.Id != contact.Id)) ViewModel.SelectedContacts.Add(contact);

        if (selected.Count == 0) ViewModel.SelectedContact = null!;
        else if (ViewModel.SelectedContact is null || !ids.Contains(ViewModel.SelectedContact.Id)) ViewModel.SelectedContact = selected[0];
    }

    private void MaybeLoadMore()
    {
        if (_released || _searchResults is not null || !ViewModel.LoadMoreContactsCommand.CanExecute(null)) return;
        var visible = _scroll.DocumentVisibleRect;
        var height = _table.Frame.Height;
        if (height <= 0 || visible.GetMaxY() < height - 240) return;
        Observe(ViewModel.LoadMoreContactsCommand.ExecuteAsync(null));
    }

    private void ToggleFavorite(AccountContactViewModel contact) => Observe(ViewModel.ToggleFavoriteCommand.ExecuteAsync(contact));

    // ---- Search (list field and the shell's title bar search) ----

    private async Task RunSearchAsync(string text, bool select)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            _searchResults = null;
            ScheduleReload();
            return;
        }
        var cancellation = _searchCancellation = new CancellationTokenSource();
        try
        {
            if (!select) await Task.Delay(150, cancellation.Token);
            var results = await ViewModel.SearchContactsAsync(text, 200);
            if (cancellation.IsCancellationRequested || _released) return;
            _searchResults = results;
            RebuildEntries();
            if (select && results.Count > 0)
            {
                var loaded = await ViewModel.LoadAndSelectContactAsync(results[0].Id);
                if (loaded is not null) ViewModel.SelectedContact = loaded;
            }
        }
        catch (OperationCanceledException) { }
    }

    public Task SearchTextChangedAsync(string text)
    {
        _search.StringValue = text ?? string.Empty;
        return RunSearchAsync(text, select: false);
    }

    public Task SearchSubmittedAsync(string text)
    {
        _search.StringValue = text ?? string.Empty;
        return RunSearchAsync(text, select: true);
    }

    public Task SearchClearedAsync()
    {
        _search.StringValue = string.Empty;
        return RunSearchAsync(string.Empty, select: false);
    }

    // ---- Context menu (Windows ContactCardMenuFlyout essentials) ----

    private void PopulateContextMenu(NSMenu menu)
    {
        menu.RemoveAllItems();
        nint clicked = _table.ClickedRow;
        if (clicked < 0 || clicked >= _entries.Count || _entries[(int)clicked].Contact is not { } contact) return;
        if (!_table.IsRowSelected(clicked)) SelectRow(contact, scroll: false);
        menu.AddItem(MenuItem(Translator.ContactAction_SendMail, WinoIconGlyph.Mail, () => ViewModel.ComposeToContactCommand.Execute(contact), contact.CanSendMail));
        menu.AddItem(MenuItem(Translator.ContactAction_Edit, WinoIconGlyph.Edit, () => ViewModel.EditContactCommand.Execute(contact), contact.IsEditable));
        menu.AddItem(MenuItem(contact.FavoriteActionText, contact.IsFavorite ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star, () => ToggleFavorite(contact), true));
        if (ViewModel.SelectedFilter?.IsList == true)
            menu.AddItem(MenuItem(Translator.ContactAction_RemoveFromList, WinoIconGlyph.List, () => Observe(ViewModel.RemoveFromCurrentListCommand.ExecuteAsync(contact)), true));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(MenuItem(Translator.ContactAction_Delete, WinoIconGlyph.Delete, () => Observe(ViewModel.DeleteContactCommand.ExecuteAsync(contact)), contact.IsEditable));
    }

    private static NSMenuItem MenuItem(string title, WinoIconGlyph glyph, Action action, bool enabled)
    {
        var item = new NSMenuItem(title, (_, _) => action()) { Image = WinoIcons.Image(glyph, 14), Enabled = enabled };
        return item;
    }

    // ---- Table plumbing ----

    private sealed class ContactsTableDataSource(ContactsPageViewController owner) : NSTableViewDataSource
    {
        public override nint GetRowCount(NSTableView tableView) => owner._entries.Count;
    }

    private sealed class ContactsTableDelegate(ContactsPageViewController owner) : NSTableViewDelegate
    {
        public override bool IsGroupRow(NSTableView tableView, nint row) => owner._entries[(int)row].Contact is null;

        public override bool ShouldSelectRow(NSTableView tableView, nint row) => owner._entries[(int)row].Contact is not null;

        public override nfloat GetRowHeight(NSTableView tableView, nint row)
            => (nfloat)(owner._entries[(int)row].Contact is null ? WinoContactStyle.HeaderRowHeight : WinoContactStyle.RowHeight);

        public override NSTableRowView CoreGetRowView(NSTableView tableView, nint row)
        {
            var view = tableView.MakeView(WinoContactTableRowView.ReuseIdentifier, this) as WinoContactTableRowView ?? new WinoContactTableRowView();
            view.IsHeader = owner._entries[(int)row].Contact is null;
            return view;
        }

        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn tableColumn, nint row)
        {
            var entry = owner._entries[(int)row];
            if (entry.Contact is null)
            {
                var header = tableView.MakeView(WinoContactGroupHeaderView.ReuseIdentifier, this) as WinoContactGroupHeaderView ?? new WinoContactGroupHeaderView();
                header.Title = entry.Letter;
                return header;
            }
            var cell = tableView.MakeView(WinoContactRowView.ReuseIdentifier, this) as WinoContactRowView ?? new WinoContactRowView();
            var contact = entry.Contact;
            cell.Bind(contact, () => new WinoContactRowModel(contact.Name, contact.SecondaryValue, contact.SourceLabel, contact.Address,
                contact.IsFavorite, contact.Categories, owner.LoadPicture(contact)), () => owner.ToggleFavorite(contact));
            return cell;
        }

        public override void SelectionDidChange(NSNotification notification) => owner.SelectionChanged();
    }

    private sealed class ContactsMenuDelegate(ContactsPageViewController owner) : NSMenuDelegate
    {
        public override void MenuWillOpen(NSMenu menu) => owner.PopulateContextMenu(menu);
    }

    // ---- Debug bridge ----

    private void RegisterDebugCommands()
    {
#if DEBUG
        MacDebugBridge.Register("contacts-select", args =>
        {
            int index = args.Length > 0 ? int.Parse(args[0]) : 0;
            var contact = _entries.Where(entry => entry.Contact is not null).Skip(index).FirstOrDefault()?.Contact;
            if (contact is null) return Task.FromResult("no contact");
            SelectRow(contact);
            ViewModel.SelectedContact = contact;
            return Task.FromResult("ok " + contact.Name);
        });
        MacDebugBridge.Register("contacts-edit", _ =>
        {
            if (ViewModel.SelectedContact is not { } contact) return Task.FromResult("no selection");
            ViewModel.EditContactCommand.Execute(contact);
            return Task.FromResult("ok");
        });
        MacDebugBridge.Register("contacts-new", _ => { ViewModel.AddContactCommand.Execute(null); return Task.FromResult("ok"); });
        MacDebugBridge.Register("contacts-search", async args => { await SearchTextChangedAsync(string.Join(' ', args)); return "ok " + (_searchResults?.Count ?? 0); });
        MacDebugBridge.Register("contacts-count", _ => Task.FromResult($"{ViewModel.Contacts.Count} contacts, {_entries.Count} rows, loading={ViewModel.IsLoading}, blocked={ViewModel.Readiness.IsBlocked}"));
#endif
    }
}
