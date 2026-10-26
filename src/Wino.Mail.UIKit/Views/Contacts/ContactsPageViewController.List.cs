using System.Collections.Specialized;
using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Contacts;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;
using Wino.Mail.ViewModels;

namespace Wino.Mail.UIKit.Views.Contacts;

public abstract partial class ContactsPageViewController
{
    /// <summary>One letter section of the table.</summary>
    private sealed record Section(string Letter, List<AccountContactViewModel> Contacts);

    private readonly List<Section> _sections = new();
    private readonly List<ContactGroup> _observedGroups = new();
    private readonly UITableView _table = new(CGRect.Empty, UITableViewStyle.Plain);
    private readonly UIRefreshControl _refresh = new();
    private readonly UIActivityIndicatorView _loading = new(UIActivityIndicatorViewStyle.Medium) { TranslatesAutoresizingMaskIntoConstraints = false, HidesWhenStopped = true };
    private readonly UIActivityIndicatorView _loadingMore = new(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true };
    private WinoStateView? _empty;
    private UISearchController? _searchController;
    private SearchHandler? _searchHandler;
    private IReadOnlyList<AccountContactViewModel>? _searchResults;
    private CancellationTokenSource? _searchCancellation;
    private bool _reloadScheduled;
    private bool _restoringSelection;
    private bool _listBound;

    protected UITableView Table => _table;

    private void BuildList(UIView host)
    {
        _table.BackgroundColor = UIColor.Clear;
        _table.SeparatorStyle = UITableViewCellSeparatorStyle.None;
        _table.SectionHeaderTopPadding = 0;
        _table.SectionIndexColor = WinoStyle.Accent;
        _table.SectionIndexBackgroundColor = UIColor.Clear;
        _table.KeyboardDismissMode = UIScrollViewKeyboardDismissMode.OnDrag;
        _table.RegisterClassForCellReuse(typeof(WinoContactRowCell), WinoContactRowCell.ReuseIdentifier);
        _table.RegisterClassForHeaderFooterViewReuse(typeof(WinoContactGroupHeaderView), WinoContactGroupHeaderView.ReuseIdentifier);
        _table.DataSource = new ContactsSource(this);
        _table.Delegate = new ContactsDelegate(this);
        _table.ContentInset = new UIEdgeInsets(4, 0, 8, 0);
        _table.TableFooterView = _loadingMore;
        _loadingMore.Frame = new CGRect(0, 0, 0, 0);
        _refresh.ValueChanged += (_, _) => Refresh();
        _table.RefreshControl = _refresh;
        WinoAccessibility.Label(_table, Translator.ContactsPage_Title);
        WinoLayout.Fill(_table, host, 0, 4, 0, 4);

        var addFirst = new UIButton(UIButtonType.System);
        addFirst.SetTitle(Translator.ContactsPage_AddFirstContact, UIControlState.Normal);
        addFirst.PrimaryActionTriggered += (_, _) => AddContact();
        _empty = new WinoStateView(WinoIconGlyph.People, Translator.ContactsPage_NoContacts, null) { Hidden = true };
        _empty.Add(addFirst);
        _empty.Place(host);
        _empty.UserInteractionEnabled = true;

        host.AddSubview(_loading);
        NSLayoutConstraint.ActivateConstraints(
        [
            _loading.CenterXAnchor.ConstraintEqualTo(host.CenterXAnchor),
            _loading.CenterYAnchor.ConstraintEqualTo(host.CenterYAnchor)
        ]);
    }

    private void BindList()
    {
        if (_listBound) return;
        _listBound = true;
        ViewModel.ContactGroups.CollectionChanged += GroupsChanged;
        ViewModel.Contacts.CollectionChanged += ContactsChanged;
        ObserveGroups();
        Bind(nameof(ViewModel.IsLoading), static vm => vm.IsLoading, loading =>
        {
            if (loading && !_refresh.Refreshing) _loading.StartAnimating(); else _loading.StopAnimating();
            UpdateEmptyState();
        });
        Bind(nameof(ViewModel.IsLoadingMore), static vm => vm.IsLoadingMore, more =>
        {
            _loadingMore.Frame = new CGRect(0, 0, _table.Bounds.Width, more ? 44 : 0);
            if (more) _loadingMore.StartAnimating(); else _loadingMore.StopAnimating();
            _table.TableFooterView = _loadingMore;
        });
        Bind(nameof(ViewModel.IsRefreshing), static vm => vm.IsRefreshing, refreshing => { if (!refreshing && _refresh.Refreshing) _refresh.EndRefreshing(); });
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
    }

    private void Refresh()
    {
        if (ViewModel.RefreshContactsCommand.CanExecute(null)) Observe(RefreshAsync());
        else _refresh.EndRefreshing();
    }

    private async Task RefreshAsync()
    {
        try { await ViewModel.RefreshContactsCommand.ExecuteAsync(null); }
        finally { OnUI(() => { if (_refresh.Refreshing) _refresh.EndRefreshing(); }); }
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

    /// <summary>Coalesces burst changes into one table reload on the next main-loop turn.</summary>
    private void ScheduleReload()
    {
        if (_reloadScheduled || IsReleased) return;
        _reloadScheduled = true;
        NSRunLoop.Main.BeginInvokeOnMainThread(() =>
        {
            _reloadScheduled = false;
            if (!IsReleased && _listBound) RebuildSections();
        });
    }

    private void RebuildSections()
    {
        _sections.Clear();
        if (_searchResults is { } results)
        {
            Section? current = null;
            foreach (var contact in results)
            {
                var letter = contact.InitialLetter ?? string.Empty;
                if (current is null || current.Letter != letter)
                {
                    current = new Section(letter, new List<AccountContactViewModel>());
                    _sections.Add(current);
                }
                current.Contacts.Add(contact);
            }
        }
        else
        {
            foreach (var group in ViewModel.ContactGroups)
                if (group.Count > 0) _sections.Add(new Section(group.Key, group.ToList()));
        }
        _restoringSelection = true;
        try
        {
            _table.ReloadData();
            SelectRow(ViewModel.SelectedContact, scroll: false);
        }
        finally { _restoringSelection = false; }
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        if (_empty is null) return;
        bool empty = _searchResults is null ? ViewModel.IsEmpty : _searchResults.Count == 0;
        _empty.Hidden = !empty || ViewModel.IsLoading;
    }

    private AccountContactViewModel? ContactAt(NSIndexPath indexPath)
    {
        if (indexPath.Section < 0 || indexPath.Section >= _sections.Count) return null;
        var contacts = _sections[(int)indexPath.Section].Contacts;
        return indexPath.Row >= 0 && indexPath.Row < contacts.Count ? contacts[(int)indexPath.Row] : null;
    }

    private NSIndexPath? IndexPathOf(AccountContactViewModel? contact)
    {
        if (contact is null) return null;
        for (int section = 0; section < _sections.Count; section++)
        {
            var index = _sections[section].Contacts.FindIndex(item => item.Id == contact.Id);
            if (index >= 0) return NSIndexPath.FromRowSection(index, section);
        }
        return null;
    }

    /// <summary>Mirrors the ViewModel selection into the table (the iPhone keeps no visible selection once it returns).</summary>
    private void SelectRow(AccountContactViewModel? contact, bool scroll = true)
    {
        var previous = _restoringSelection;
        _restoringSelection = true;
        try
        {
            if (IndexPathOf(contact) is { } path)
            {
                if (_table.IndexPathForSelectedRow is { } selected && selected.Section == path.Section && selected.Row == path.Row) return;
                _table.SelectRow(path, false, scroll ? UITableViewScrollPosition.None : UITableViewScrollPosition.None);
            }
            else if (_table.IndexPathForSelectedRow is { } selected)
            {
                _table.DeselectRow(selected, false);
            }
        }
        finally { _restoringSelection = previous; }
    }

    /// <summary>Clears the table's visible selection (the iPhone, back from the card).</summary>
    protected void DeselectVisibleRow()
    {
        if (_table.IndexPathForSelectedRow is { } selected) _table.DeselectRow(selected, true);
    }

    private void RowTapped(NSIndexPath indexPath)
    {
        if (_restoringSelection || ContactAt(indexPath) is not { } contact) return;
        ViewModel.SelectedContacts.Clear();
        ViewModel.SelectedContacts.Add(contact);
        ViewModel.SelectedContact = contact;
        ContactActivated(contact);
    }

    private void CheckLoadMore()
    {
        if (IsReleased || _searchResults is not null || !ViewModel.LoadMoreContactsCommand.CanExecute(null)) return;
        var offset = _table.ContentOffset.Y + _table.Bounds.Height;
        if (_table.ContentSize.Height <= 0 || offset < _table.ContentSize.Height - 240) return;
        Observe(ViewModel.LoadMoreContactsCommand.ExecuteAsync(null));
    }

    private void ToggleFavorite(AccountContactViewModel contact) => Observe(ViewModel.ToggleFavoriteCommand.ExecuteAsync(contact));

    private void BindCell(WinoContactRowCell cell, AccountContactViewModel contact)
        => cell.Bind(contact, () => new WinoContactRowModel(contact.Name, contact.SecondaryValue, contact.SourceLabel, contact.Address,
            contact.IsFavorite, contact.Categories, LoadPicture(contact)), () => ToggleFavorite(contact));

    // ---- Search (the iPhone search controller and the iPad toolbar field) ----

    private void BuildSearchController()
    {
        _searchHandler = new SearchHandler(this);
        _searchController = new UISearchController((UIViewController?)null)
        {
            ObscuresBackgroundDuringPresentation = false,
            SearchResultsUpdater = _searchHandler
        };
        _searchController.SearchBar.Placeholder = Translator.ContactsPage_SearchPlaceholder;
        NavigationItem.SearchController = _searchController;
        NavigationItem.HidesSearchBarWhenScrolling = true;
        DefinesPresentationContext = true;
    }

    private async Task RunSearchAsync(string text, bool select)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            if (_searchResults is null) return;
            _searchResults = null;
            ScheduleReload();
            return;
        }
        var cancellation = _searchCancellation = new CancellationTokenSource();
        try
        {
            if (!select) await Task.Delay(ContactsPageViewModel.SearchDebounce, cancellation.Token);
            var results = await ViewModel.SearchContactsAsync(text, ContactsPageViewModel.SearchLimit);
            if (cancellation.IsCancellationRequested || IsReleased) return;
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                _searchResults = results;
                RebuildSections();
            });
            if (select && results.Count > 0 && await ViewModel.LoadAndSelectContactAsync(results[0].Id) is { } loaded)
                await Dispatcher.ExecuteOnUIThread(() => ContactActivated(loaded));
        }
        catch (OperationCanceledException) { }
    }

    public Task SearchTextChangedAsync(string text) => RunSearchAsync(text ?? string.Empty, select: false);

    public Task SearchSubmittedAsync(string text) => RunSearchAsync(text ?? string.Empty, select: true);

    public Task SearchClearedAsync() => RunSearchAsync(string.Empty, select: false);

    private sealed class SearchHandler(ContactsPageViewController owner) : UISearchResultsUpdating
    {
        public override void UpdateSearchResultsForSearchController(UISearchController searchController)
            => owner.Observe(owner.RunSearchAsync(searchController.SearchBar.Text ?? string.Empty, select: false));
    }

    // ---- Table plumbing ----

    private sealed class ContactsSource(ContactsPageViewController owner) : UITableViewDataSource
    {
        public override nint NumberOfSections(UITableView tableView) => owner._sections.Count;

        public override nint RowsInSection(UITableView tableView, nint section)
            => section >= 0 && section < owner._sections.Count ? owner._sections[(int)section].Contacts.Count : 0;

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var cell = (WinoContactRowCell)tableView.DequeueReusableCell(WinoContactRowCell.ReuseIdentifier, indexPath);
            if (owner.ContactAt(indexPath) is { } contact) owner.BindCell(cell, contact);
            return cell;
        }

        public override string[] SectionIndexTitles(UITableView tableView)
            => owner._searchResults is null ? owner._sections.Select(section => section.Letter).ToArray() : [];

        public override nint SectionFor(UITableView tableView, string title, nint atIndex) => atIndex;
    }

    private sealed class ContactsDelegate(ContactsPageViewController owner) : UITableViewDelegate
    {
        public override nfloat GetHeightForRow(UITableView tableView, NSIndexPath indexPath) => (nfloat)WinoContactStyle.RowHeight;

        public override nfloat GetHeightForHeader(UITableView tableView, nint section) => (nfloat)WinoContactStyle.HeaderRowHeight;

        public override nfloat GetHeightForFooter(UITableView tableView, nint section) => 0;

        public override UIView? GetViewForHeader(UITableView tableView, nint section)
        {
            if (tableView.DequeueReusableHeaderFooterView(WinoContactGroupHeaderView.ReuseIdentifier) is not WinoContactGroupHeaderView header)
                header = new WinoContactGroupHeaderView();
            header.Title = section >= 0 && section < owner._sections.Count ? owner._sections[(int)section].Letter : string.Empty;
            return header;
        }

        public override void RowSelected(UITableView tableView, NSIndexPath indexPath) => owner.RowTapped(indexPath);

        public override UISwipeActionsConfiguration? GetLeadingSwipeActionsConfiguration(UITableView tableView, NSIndexPath indexPath)
            => owner.SwipeActions(indexPath, leading: true);

        public override UISwipeActionsConfiguration? GetTrailingSwipeActionsConfiguration(UITableView tableView, NSIndexPath indexPath)
            => owner.SwipeActions(indexPath, leading: false);

        public override UIContextMenuConfiguration? GetContextMenuConfiguration(UITableView tableView, NSIndexPath indexPath, CGPoint point)
            => owner.ContextMenu(indexPath);

        public override void CellDisplayingEnded(UITableView tableView, UITableViewCell cell, NSIndexPath indexPath)
        {
            if (cell is WinoContactRowCell row) row.Unbind();
        }

        public override void WillDisplay(UITableView tableView, UITableViewCell cell, NSIndexPath indexPath)
        {
            if (cell is WinoContactRowCell row && owner.ContactAt(indexPath) is { } contact) owner.BindCell(row, contact);
        }

        public override void Scrolled(UIScrollView scrollView) => owner.CheckLoadMore();
    }
}
