using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Contacts;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Lists;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Contacts;

/// <summary>
/// The contact list (macOS ContactsPageViewController.List): the ViewModel's letter groups flattened into one row array,
/// letters as GTK sections, multi-selection mirrored into the ViewModel, load more near the end, search results in
/// place of the groups, and the loading and empty states. Rebuilds are coalesced to one per main-loop turn.
/// </summary>
internal sealed partial class ContactsPageView
{
    private const double LoadMoreDistance = 240;

    private readonly Dictionary<AccountContactViewModel, int> _sectionOf = new(ReferenceEqualityComparer.Instance);
    private readonly List<string> _letters = [];
    private readonly List<ContactGroup> _observedGroups = [];
    private ObservableListModel<AccountContactViewModel> _model;
    private ListSections<AccountContactViewModel> _sections;
    private Gtk.MultiSelection _selection;
    private WinoCellFactory<ContactCell, AccountContactViewModel> _cells;
    private WinoHeaderFactory<AccountContactViewModel> _headers;
    private Gtk.ListView _list;
    private Gtk.ScrolledWindow _scroller;
    private Gtk.Widget _empty;
    private Adw.Spinner _loading;
    private Gtk.Widget _loadingMore;
    private Gtk.Button _addFirst;
    private MainLoop.Coalescer _reflow;
    private LoadMoreTrigger _loadMore;
    private AccountContactViewModel[]? _searchResults;
    private bool _restoringSelection;
    private long _firstRowsAt = -1;
    private long _boundAt;

    internal Gtk.ListView List => _list;

    internal Gtk.MultiSelection Selection => _selection;

    internal WinoCellFactory<ContactCell, AccountContactViewModel> Cells => _cells;

    internal ListSections<AccountContactViewModel> Sections => _sections;

    internal int RowCount => _model.Count;

    internal int SectionCount => _letters.Count;

    internal bool IsShowingSearchResults => _searchResults is not null;

    internal double FirstRowsMilliseconds => _firstRowsAt < 0 ? -1 : (_firstRowsAt - _boundAt) / (double)TimeSpan.TicksPerMillisecond;

    [MemberNotNull(nameof(_model), nameof(_sections), nameof(_selection), nameof(_cells), nameof(_headers), nameof(_list), nameof(_scroller),
        nameof(_empty), nameof(_loading), nameof(_loadingMore), nameof(_addFirst), nameof(_reflow), nameof(_loadMore))]
    private Gtk.Widget BuildList()
    {
        var self = _self;
        _model = Bindings.Own(new ObservableListModel<AccountContactViewModel>(Dispatcher, ReportError));
        _sections = Bindings.Own(new ListSections<AccountContactViewModel>(_model.Store,
            (a, b) => self.Value is { } view ? view.SectionOf(a).CompareTo(view.SectionOf(b)) : 0, null, ReportError));
        _selection = Gtk.MultiSelection.New(_sections.Model);
        _cells = Bindings.Own(new WinoCellFactory<ContactCell, AccountContactViewModel>(
            () => new ContactCell(_pictures, Dispatcher, contact => self.Value?.ToggleFavorite(contact)), ReportError)
        {
            Setup = (cell, _) =>
            {
                if (self.Value is not { } view) return;
                view.ContextCellSetup(cell);
                view.DragCellSetup(cell);
            },
            Teardown = cell =>
            {
                if (self.Value is { } view)
                {
                    view.DragCellTeardown(cell);
                    view.ContextCellTeardown(cell);
                }
                cell.Row.Dispose();
            },
        });
        _headers = Bindings.Own(new WinoHeaderFactory<AccountContactViewModel>(WinoContactGroupHeader.Create,
            (widget, item, _) => self.Value?.BindHeader(widget, item), ReportError));

        _list = Gtk.ListView.New(_selection, _cells.Factory);
        _list.SetHeaderFactory(_headers.Factory);
        _list.SetSingleClickActivate(false);
        _list.SetShowSeparators(false);
        _list.SetEnableRubberband(false);
        _list.AddCssClass(WinoCssClass.ContactList);
        WinoA11y.Label(_list, Translator.Accessibility_ContactList);

        _scroller = Gtk.ScrolledWindow.New();
        _scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        _scroller.SetChild(_list);
        _scroller.SetVexpand(true);
        _scroller.SetHexpand(true);

        var spinner = Adw.Spinner.New();
        spinner.SetSizeRequest(16, 16);
        spinner.SetHalign(Gtk.Align.Center);
        spinner.SetMarginTop(4);
        spinner.SetMarginBottom(8);
        spinner.SetVisible(false);
        _loadingMore = spinner;

        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        column.Append(_scroller);
        column.Append(_loadingMore);

        _loading = Adw.Spinner.New();
        _loading.SetSizeRequest(32, 32);
        _loading.SetHalign(Gtk.Align.Center);
        _loading.SetValign(Gtk.Align.Center);
        _loading.SetVisible(false);

        _empty = BuildEmpty();

        var pane = Gtk.Overlay.New();
        pane.SetChild(column);
        pane.AddOverlay(_empty);
        pane.AddOverlay(_loading);

        _reflow = Bindings.Own(new MainLoop.Coalescer(() => self.Value?.Flatten()));
        _loadMore = Bindings.Own(ListViews.LoadMoreNearEnd(_scroller, LoadMoreDistance, () => self.Value?.LoadMore()));
        Bindings.Own(ListViews.KeepSelection(_model, _selection, item => self.Value?._sections.PositionOf(item)));
        var selection = _selection;
        Bindings.Own(GtkSignals.Connect<Gtk.SelectionModel, Gtk.SelectionModel.SelectionChangedSignalArgs>(
            h => selection.OnSelectionChanged += h, h => selection.OnSelectionChanged -= h,
            (_, _) => self.Value?.SelectionChanged()));
        return pane;
    }

    [MemberNotNull(nameof(_addFirst))]
    private Gtk.Widget BuildEmpty()
    {
        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        column.SetHalign(Gtk.Align.Center);
        column.SetValign(Gtk.Align.Center);
        column.AddCssClass(WinoCssClass.ContactEmpty);
        var icon = new WinoIcon(WinoIconGlyph.People, 72).Root;
        icon.AddCssClass("dim-label");
        column.Append(icon);
        var text = Gtk.Label.New(Translator.ContactsPage_NoContacts);
        text.AddCssClass("dim-label");
        text.SetWrap(true);
        text.SetJustify(Gtk.Justification.Center);
        column.Append(text);
        _addFirst = Gtk.Button.NewWithLabel(Translator.ContactsPage_AddFirstContact);
        _addFirst.AddCssClass("suggested-action");
        _addFirst.AddCssClass("pill");
        _addFirst.SetHalign(Gtk.Align.Center);
        WinoA11y.Rename(_addFirst, Translator.ContactsPage_AddFirstContact);
        column.Append(_addFirst);
        column.SetVisible(false);
        return column;
    }

    private void BindList()
    {
        _boundAt = DateTime.UtcNow.Ticks;
        _firstRowsAt = -1;
        Bind.BindCommand(_addFirst, ViewModel.AddContactCommand);
        Bind.BindCollection(ViewModel.ContactGroups, () =>
        {
            ObserveGroups();
            _reflow.Request();
        });
        Bind.BindCollection(ViewModel.Contacts, UpdateEmptyState);
        Bindings.Own(new ActionDisposable(UnobserveGroups));
        Bind.Bind(nameof(ContactsPageViewModel.IsLoading), static vm => vm.IsLoading, loading =>
        {
            _loading.SetVisible(loading);
            UpdateEmptyState();
        });
        Bind.Bind(nameof(ContactsPageViewModel.IsLoadingMore), static vm => vm.IsLoadingMore, _loadingMore.SetVisible);
        Bind.Bind(nameof(ContactsPageViewModel.SelectedContact), static vm => vm.SelectedContact, _ => ApplySelectedContact(scroll: true));
        Bind.Bind(nameof(ContactsPageViewModel.IsSelectionMode), static vm => vm.IsSelectionMode, selectionMode =>
        {
            if (!selectionMode && ListViews.SelectedPositions(_selection).Length > 1) _selection.UnselectAll();
        });
        Bind.Bind(nameof(ContactsPageViewModel.HasMoreContacts), static vm => vm.HasMoreContacts, _ => _loadMore.Check());
        var self = _self;
        Action<Guid> loaded = id => self.Value?._cells.ForEachBound((cell, item) =>
        {
            if (item.ContactPictureFileId == id) cell.Refresh();
        });
        _pictures.Loaded += loaded;
        Bindings.Own(new ActionDisposable(() => _pictures.Loaded -= loaded));
    }

    private void ObserveGroups()
    {
        UnobserveGroups();
        foreach (var group in ViewModel.ContactGroups)
        {
            group.CollectionChanged += GroupChanged;
            _observedGroups.Add(group);
        }
    }

    private void UnobserveGroups()
    {
        foreach (var group in _observedGroups) group.CollectionChanged -= GroupChanged;
        _observedGroups.Clear();
    }

    private void GroupChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        var self = _self;
        _ = Dispatcher.ExecuteOnUIThread(() => self.Value?._reflow.Request());
    }

    private int SectionOf(AccountContactViewModel item) => _sectionOf.TryGetValue(item, out var index) ? index : int.MaxValue;

    private void Flatten()
    {
        if (IsReleased) return;
        var rows = new List<AccountContactViewModel>(Math.Max(_model.Count, 64));
        _sectionOf.Clear();
        _letters.Clear();
        if (_searchResults is { } results)
        {
            string? letter = null;
            foreach (var contact in results)
            {
                var initial = contact.InitialLetter ?? string.Empty;
                if (letter is null || !string.Equals(initial, letter, StringComparison.Ordinal))
                {
                    letter = initial;
                    _letters.Add(initial);
                }
                _sectionOf[contact] = _letters.Count - 1;
                rows.Add(contact);
            }
        }
        else
        {
            foreach (var group in ViewModel.ContactGroups)
            {
                if (group.Count == 0) continue;
                _letters.Add(group.Key ?? string.Empty);
                var section = _letters.Count - 1;
                foreach (var contact in group)
                {
                    if (contact is null || _sectionOf.ContainsKey(contact)) continue;
                    _sectionOf[contact] = section;
                    rows.Add(contact);
                }
            }
        }
        var atTop = _scroller.GetVadjustment().GetValue() <= 0.5;
        _model.SetItems(rows);
        if (atTop && rows.Count > 0) KeepAtTop();
        if (rows.Count > 0 && _firstRowsAt < 0)
        {
            _firstRowsAt = DateTime.UtcNow.Ticks;
            Serilog.Log.Information("Contacts list: first {Count} rows {Milliseconds:0} ms after the page bound.", rows.Count, FirstRowsMilliseconds);
        }
        UpdateEmptyState();
        ApplySelectedContact(scroll: false);
        MirrorSelection();
        _loadMore.Check();
    }

    // GTK keeps the first row at the top edge when rows arrive, which hides the first letter header.
    private void KeepAtTop()
    {
        var self = _self;
        var frames = 0;
        _list.AddTickCallback((_, _) =>
        {
            if (self.Value is not { } view) return false;
            var adjustment = view._scroller.GetVadjustment();
            if (adjustment.GetValue() > 0) adjustment.SetValue(adjustment.GetLower());
            return ++frames < 2;
        });
    }

    private void BindHeader(Gtk.Widget widget, AccountContactViewModel item)
    {
        if (_sectionOf.TryGetValue(item, out var section) && section >= 0 && section < _letters.Count)
            WinoContactGroupHeader.Apply(widget, _letters[section]);
    }

    private void ShowSearchResults(AccountContactViewModel[]? results)
    {
        if (IsReleased) return;
        if (results is null && _searchResults is null) return;
        _searchResults = results;
        Flatten();
    }

    private void UpdateEmptyState()
    {
        var empty = _searchResults is null ? ViewModel.IsEmpty : _searchResults.Length == 0;
        var loading = ViewModel.IsLoading;
        _empty.SetVisible(empty && !loading);
        _addFirst.SetVisible(_searchResults is null);
        _scroller.SetVisible(!(empty && !loading));
    }

    private void LoadMore()
    {
        if (IsReleased || _searchResults is not null) return;
        var command = ViewModel.LoadMoreContactsCommand;
        if (command.CanExecute(null)) Observe(command.ExecuteAsync(null));
    }

    private void ToggleFavorite(AccountContactViewModel contact)
    {
        var command = ViewModel.ToggleFavoriteCommand;
        if (command.CanExecute(contact)) Observe(command.ExecuteAsync(contact));
    }

    private void SelectionChanged()
    {
        if (_model.IsApplying || IsReleased) return;
        var selected = MirrorSelection();
        if (_restoringSelection) return;
        var ids = new HashSet<Guid>(selected.Select(static contact => contact.Id));
        AccountContactViewModel? current = ViewModel.SelectedContact;
        if (selected.Length == 0)
        {
            AccountContactViewModel? none = null;
            if (current is not null) ViewModel.SelectedContact = none;
        }
        else if (current is null || !ids.Contains(current.Id))
        {
            ViewModel.SelectedContact = selected[0];
        }
    }

    /// <summary>Copies the list selection into <c>SelectedContacts</c>, also for a selection the view made itself.</summary>
    private AccountContactViewModel[] MirrorSelection()
    {
        var selected = ListViews.SelectedValues<AccountContactViewModel>(_selection);
        var ids = new HashSet<Guid>(selected.Select(static contact => contact.Id));
        var target = ViewModel.SelectedContacts;
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (target[index] is not { } existing || !ids.Contains(existing.Id)) target.RemoveAt(index);
        }
        var present = new HashSet<Guid>(target.Select(static contact => contact.Id));
        foreach (var contact in selected)
        {
            if (present.Add(contact.Id)) target.Add(contact);
        }
        return selected;
    }

    internal void ApplySelectedContact(bool scroll)
    {
        if (IsReleased) return;
        AccountContactViewModel? contact = ViewModel.SelectedContact;
        if (contact is null)
        {
            if (!ViewModel.IsSelectionMode && ListViews.SelectedPositions(_selection).Length > 0) Restore(() => _selection.UnselectAll());
            return;
        }
        if (PositionOf(contact) is not { } position) return;
        if (_selection.IsSelected(position)) return;
        Restore(() =>
        {
            if (ViewModel.IsSelectionMode) _selection.SelectItem(position, false);
            else ListViews.Select(_selection, [position]);
        });
        if (scroll && ItemAtPosition(position) is { } item && _cells.CellOf(item) is null) ListViews.ScrollTo(_list, position);
    }

    internal uint? PositionOf(AccountContactViewModel contact)
    {
        if (_sections.PositionOf(contact) is { } position) return position;
        for (var index = 0; index < _model.Count; index++)
        {
            if (_model[index].Id == contact.Id) return _sections.PositionOf(_model[index]);
        }
        return null;
    }

    internal AccountContactViewModel? ItemAtPosition(uint position) => _sections.ItemAt(position);

    private void Restore(Action action)
    {
        _restoringSelection = true;
        try { action(); }
        finally { _restoringSelection = false; }
    }
}
