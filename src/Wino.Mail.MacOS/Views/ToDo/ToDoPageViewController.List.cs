using System.Collections.Specialized;
using System.ComponentModel;
using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.ToDo;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.ToDo;

public sealed partial class ToDoPageViewController
{
    /// <summary>A table row: a group header or a task.</summary>
    private sealed record ListEntry(TaskGroup? Group, TaskItemViewModel? Item);

    private readonly List<ListEntry> _entries = new();
    private readonly List<TaskGroup> _observedGroups = new();
    private NSTextField _title = null!;
    private NSButton _renameButton = null!;
    private NSTextField _subtitle = null!;
    private NSButton _suggestionsButton = null!;
    private NSButton _moreButton = null!;
    private NSSearchField _search = null!;
    private NSStackView _expandedFilters = null!;
    private WinoChipButton _scopeChip = null!;
    private WinoChipButton _importantChip = null!;
    private WinoChipButton _sortChip = null!;
    private WinoChipButton _clearChip = null!;
    private WinoChipButton _compactFilterChip = null!;
    private NSMenuItem _scopeCompletedItem = null!;
    private NSMenuItem _scopeAllItem = null!;
    private NSMenuItem _compactScopeCompletedItem = null!;
    private NSMenuItem _compactScopeAllItem = null!;
    private NSMenuItem _compactImportantItem = null!;
    private NSMenuItem _compactClearItem = null!;
    private TaskTableView _table = null!;
    private NSScrollView _scroll = null!;
    private NSStackView _emptyState = null!;
    private NSTextField _emptyTitle = null!;
    private NSTextField _emptyBody = null!;
    private WinoQuickAddView _quickAdd = null!;
    private NSPopover? _suggestionsPopover;
    private NSPopover? _dueDatePopover;
    private bool _reloadScheduled;
    private bool _restoringSelection;
    private bool _listBound;

    private NSView BuildListSurface()
    {
        var surface = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };

        // Header (Windows padding 24,20,20,8): title + rename, suggestions and list options on the right.
        _title = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(24, NSFontWeight.Semibold), WinoStyle.Accent);
        _title.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _renameButton = WinoToDoStyle.IconButton(WinoIconGlyph.Edit, Translator.ToDoPage_RenameList, 12, 28, 28, WinoStyle.SecondaryText);
        _renameButton.Activated += (_, _) => Observe(ViewModel.RenameSelectedListCommand.ExecuteAsync(null));
        _suggestionsButton = WinoToDoStyle.IconButton(WinoIconGlyph.Lightbulb, Translator.ToDoPage_Suggestions, 16);
        _suggestionsButton.Activated += (_, _) => ShowSuggestions();
        _moreButton = WinoToDoStyle.IconButton(WinoIconGlyph.More, Translator.ToDoPage_ListOptions, 16);
        _moreButton.Activated += (_, _) => ShowListOptions();
        var titleRow = WinoLayout.HStack(4, _title, _renameButton, WinoLayout.Spacer(), _suggestionsButton, _moreButton);
        titleRow.SetCustomSpacing(2, _suggestionsButton);

        _subtitle = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText);

        var filterRow = BuildFilterRow();
        var header = WinoLayout.VStack(2, titleRow, _subtitle, filterRow);
        header.SetCustomSpacing(12, _subtitle);
        header.EdgeInsets = new NSEdgeInsets(20, 24, 8, 20);
        titleRow.WidthAnchor.ConstraintEqualTo(header.WidthAnchor, 1, -44).Active = true;
        filterRow.WidthAnchor.ConstraintEqualTo(header.WidthAnchor, 1, -44).Active = true;

        // Grouped task cards (Windows WinoListView padding 16,0,16,4).
        _table = new TaskTableView(this)
        {
            HeaderView = null,
            Style = NSTableViewStyle.Plain,
            IntercellSpacing = new CGSize(0, 0),
            AllowsMultipleSelection = true,
            AllowsEmptySelection = true,
            AllowsTypeSelect = false,
            FloatsGroupRows = false,
            BackgroundColor = NSColor.Clear,
            ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.FirstColumnOnly,
            UsesAutomaticRowHeights = false,
            SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.None
        };
        _table.AddColumn(new NSTableColumn("task") { ResizingMask = NSTableColumnResizing.Autoresizing, Width = 400 });
        _table.DataSource = new TaskTableDataSource(this);
        _table.Delegate = new TaskTableDelegate(this);
        WinoAccessibility.Label(_table, Translator.ToDoPage_Tasks);
        _scroll = new NSScrollView
        {
            DocumentView = _table,
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            DrawsBackground = false,
            BorderType = NSBorderType.NoBorder,
            TranslatesAutoresizingMaskIntoConstraints = false,
            ContentInsets = new NSEdgeInsets(0, 16, 4, 16),
            AutomaticallyAdjustsContentInsets = false
        };

        _emptyTitle = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, maximumLines: 2);
        _emptyTitle.Alignment = NSTextAlignment.Center;
        _emptyBody = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        _emptyBody.Alignment = NSTextAlignment.Center;
        _emptyState = WinoLayout.VStack(6, _emptyTitle, _emptyBody);
        _emptyState.Alignment = NSLayoutAttribute.CenterX;
        _emptyState.Hidden = true;

        // Quick add (Windows margin 20,4,20,20).
        _quickAdd = new WinoQuickAddView(Translator.ToDoPage_AddTask);
        _quickAdd.Submitted += (_, _) => SubmitQuickAdd();
        _quickAdd.TextChanged += (_, _) => ViewModel.ComposerText = _quickAdd.Text;
        _quickAdd.FocusChanged += (_, _) =>
        {
            if (_quickAdd.IsFocused) ViewModel.IsComposerExpanded = true;
            else if (string.IsNullOrWhiteSpace(ViewModel.ComposerText)) ViewModel.IsComposerExpanded = false;
        };

        surface.AddSubview(header);
        surface.AddSubview(_scroll);
        surface.AddSubview(_emptyState);
        surface.AddSubview(_quickAdd);
        NSLayoutConstraint.ActivateConstraints(
        [
            header.TopAnchor.ConstraintEqualTo(surface.TopAnchor),
            header.LeadingAnchor.ConstraintEqualTo(surface.LeadingAnchor),
            header.TrailingAnchor.ConstraintEqualTo(surface.TrailingAnchor),
            _scroll.TopAnchor.ConstraintEqualTo(header.BottomAnchor),
            _scroll.LeadingAnchor.ConstraintEqualTo(surface.LeadingAnchor),
            _scroll.TrailingAnchor.ConstraintEqualTo(surface.TrailingAnchor),
            _scroll.BottomAnchor.ConstraintEqualTo(_quickAdd.TopAnchor, -4),
            _emptyState.CenterXAnchor.ConstraintEqualTo(_scroll.CenterXAnchor),
            _emptyState.CenterYAnchor.ConstraintEqualTo(_scroll.CenterYAnchor),
            _emptyState.WidthAnchor.ConstraintLessThanOrEqualTo(320),
            _quickAdd.LeadingAnchor.ConstraintEqualTo(surface.LeadingAnchor, 20),
            _quickAdd.TrailingAnchor.ConstraintEqualTo(surface.TrailingAnchor, -20),
            _quickAdd.BottomAnchor.ConstraintEqualTo(surface.BottomAnchor, -20)
        ]);
        return surface;
    }

    /// <summary>Search field, Show scope, Important toggle, Sort and Clear; one Filter chip when compact.</summary>
    private NSView BuildFilterRow()
    {
        _search = new NSSearchField { PlaceholderString = Translator.ToDoPage_FilterTasks, TranslatesAutoresizingMaskIntoConstraints = false, ControlSize = NSControlSize.Regular };
        _search.Changed += (_, _) => ViewModel.FilterText = _search.StringValue;
        _search.SearchingEnded += (_, _) => ViewModel.FilterText = _search.StringValue;
        WinoAccessibility.Label(_search, Translator.ToDoPage_FilterTasks);
        _search.WidthAnchor.ConstraintEqualTo(360).Active = true;
        _search.SetContentCompressionResistancePriority(240, NSLayoutConstraintOrientation.Horizontal);

        var scopeMenu = new NSMenu { AutoEnablesItems = false };
        scopeMenu.AddItem(MenuItem(Translator.ToDoPage_ScopeActive, () => ViewModel.SetCompletionScopeCommand.Execute("active")));
        scopeMenu.AddItem(_scopeCompletedItem = MenuItem(Translator.ToDoPage_Completed, () => ViewModel.SetCompletionScopeCommand.Execute("completed")));
        scopeMenu.AddItem(_scopeAllItem = MenuItem(Translator.ToDoPage_All, () => ViewModel.SetCompletionScopeCommand.Execute("all")));
        _scopeChip = new WinoChipButton(WinoIconGlyph.List, string.Empty, Translator.ToDoPage_ShowScope) { Menu = scopeMenu };

        _importantChip = new WinoChipButton(WinoIconGlyph.Star, Translator.ToDoPage_Important, Translator.ToDoPage_Important);
        _importantChip.Clicked += (_, _) => ViewModel.IsImportantTasksFilterSelected = !ViewModel.IsImportantTasksFilterSelected;

        _sortChip = new WinoChipButton(WinoIconGlyph.ArrowSort, string.Empty, Translator.ToDoPage_SortBy) { Menu = BuildSortMenu() };

        _clearChip = new WinoChipButton(WinoIconGlyph.Dismiss, Translator.ToDoPage_Clear, Translator.ToDoPage_ClearFilters);
        _clearChip.Clicked += (_, _) => ViewModel.ClearFiltersCommand.Execute(null);

        _expandedFilters = WinoLayout.HStack(8, _scopeChip, _importantChip, _sortChip, _clearChip);

        var compactMenu = new NSMenu { AutoEnablesItems = false };
        var showMenu = new NSMenu { AutoEnablesItems = false };
        showMenu.AddItem(MenuItem(Translator.ToDoPage_ScopeActive, () => ViewModel.SetCompletionScopeCommand.Execute("active")));
        showMenu.AddItem(_compactScopeCompletedItem = MenuItem(Translator.ToDoPage_Completed, () => ViewModel.SetCompletionScopeCommand.Execute("completed")));
        showMenu.AddItem(_compactScopeAllItem = MenuItem(Translator.ToDoPage_All, () => ViewModel.SetCompletionScopeCommand.Execute("all")));
        compactMenu.AddItem(new NSMenuItem(Translator.ToDoPage_ShowScope) { Submenu = showMenu });
        compactMenu.AddItem(_compactImportantItem = MenuItem(Translator.ToDoPage_Important, () => ViewModel.IsImportantTasksFilterSelected = !ViewModel.IsImportantTasksFilterSelected));
        compactMenu.AddItem(new NSMenuItem(Translator.ToDoPage_SortBy) { Submenu = BuildSortMenu() });
        compactMenu.AddItem(NSMenuItem.SeparatorItem);
        compactMenu.AddItem(_compactClearItem = MenuItem(Translator.ToDoPage_ClearFilters, () => ViewModel.ClearFiltersCommand.Execute(null), WinoIconGlyph.Dismiss));
        _compactFilterChip = new WinoChipButton(WinoIconGlyph.Filter, null, Translator.ToDoPage_FilterOptions) { Menu = compactMenu, Hidden = true };

        var row = WinoLayout.HStack(8, _search, _expandedFilters, WinoLayout.Spacer(), _compactFilterChip);
        return row;
    }

    private NSMenu BuildSortMenu()
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.AddItem(MenuItem(Translator.ToDoPage_SortImportance, () => ViewModel.SetSortCommand.Execute("importance")));
        menu.AddItem(MenuItem(Translator.ToDoPage_SortDueDate, () => ViewModel.SetSortCommand.Execute("due")));
        menu.AddItem(MenuItem(Translator.ToDoPage_SortMyDay, () => ViewModel.SetSortCommand.Execute("myday")));
        menu.AddItem(MenuItem(Translator.ToDoPage_SortAlphabetically, () => ViewModel.SetSortCommand.Execute("alphabetical")));
        menu.AddItem(MenuItem(Translator.ToDoPage_SortCreationDate, () => ViewModel.SetSortCommand.Execute("created")));
        return menu;
    }

    private void BindList()
    {
        if (_listBound) return;
        _listBound = true;

        Bind(nameof(ViewModel.SelectedSurfaceTitle), static vm => vm.SelectedSurfaceTitle, value => _title.StringValue = value ?? string.Empty);
        Bind(nameof(ViewModel.SelectedSurfaceSubtitle), static vm => vm.SelectedSurfaceSubtitle, value => _subtitle.StringValue = value ?? string.Empty);
        Bind(nameof(ViewModel.HasSurfaceSubtitle), static vm => vm.HasSurfaceSubtitle, value => _subtitle.Hidden = !value);
        Bind(nameof(ViewModel.CanEditSelectedList), static vm => vm.CanEditSelectedList, value => _renameButton.Hidden = !value);
        Bind(nameof(ViewModel.IsMyDaySelected), static vm => vm.IsMyDaySelected, value => _suggestionsButton.Hidden = !value);
        Bind(nameof(ViewModel.IsNamedListSelected), static vm => vm.IsNamedListSelected, value => _moreButton.Hidden = !value);
        Bind(nameof(ViewModel.FilterText), static vm => vm.FilterText, value => { if (_search.StringValue != (value ?? string.Empty)) _search.StringValue = value ?? string.Empty; });
        Bind(nameof(ViewModel.CompletionScopeDisplayText), static vm => vm.CompletionScopeDisplayText, value => _scopeChip.Title = value);
        Bind(nameof(ViewModel.SortDisplayText), static vm => vm.SortDisplayText, value => _sortChip.Title = value);
        Bind(nameof(ViewModel.IsImportantTasksFilterSelected), static vm => vm.IsImportantTasksFilterSelected, value =>
        {
            _importantChip.Checked = value;
            _importantChip.Glyph = value ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star;
            _compactImportantItem.State = value ? NSCellStateValue.On : NSCellStateValue.Off;
        });
        Bind(nameof(ViewModel.IsFiltered), static vm => vm.IsFiltered, value => { _clearChip.Enabled = value; _compactClearItem.Enabled = value; });
        Bind(nameof(ViewModel.IsCompletedScopeAvailable), static vm => vm.IsCompletedScopeAvailable, value =>
        {
            _scopeCompletedItem.Hidden = !value; _scopeAllItem.Hidden = !value;
            _compactScopeCompletedItem.Hidden = !value; _compactScopeAllItem.Hidden = !value;
        });
        Bind(nameof(ViewModel.IsCompactLayout), static vm => vm.IsCompactLayout, value => { _expandedFilters.Hidden = value; _compactFilterChip.Hidden = !value; });
        Bind(nameof(ViewModel.IsEmpty), static vm => vm.IsEmpty, value => _emptyState.Hidden = !value || _previewGroups is not null);
        Bind(nameof(ViewModel.EmptyStateTitle), static vm => vm.EmptyStateTitle, value => _emptyTitle.StringValue = value ?? string.Empty);
        Bind(nameof(ViewModel.EmptyStateBody), static vm => vm.EmptyStateBody, value => _emptyBody.StringValue = value ?? string.Empty);
        Bind(nameof(ViewModel.IsQuickAddVisible), static vm => vm.IsQuickAddVisible, value => _quickAdd.Hidden = !value);
        Bind(nameof(ViewModel.ComposerPlaceholder), static vm => vm.ComposerPlaceholder, value => _quickAdd.Placeholder = value ?? string.Empty);
        Bind(nameof(ViewModel.ComposerText), static vm => vm.ComposerText, value => _quickAdd.Text = value ?? string.Empty);
        Bind(nameof(ViewModel.CanCreateTask), static vm => vm.CanCreateTask, value => _quickAdd.Enabled = value);
        Bindings.Own(new CommandBinding(ViewModel.AddTaskCommand, () => null, _ => { }, Dispatcher, ReportError));

        ViewModel.TaskComposerFocusRequested += ComposerFocusRequested;
        ViewModel.TaskSelectionRestored += SelectionRestored;
        ((INotifyCollectionChanged)ViewModel.TaskGroups).CollectionChanged += GroupsChanged;
        WinoStyle.AccentChanged += AccentChanged;
        ObserveGroups();
        RebuildEntries();
    }

    private void ReleaseList()
    {
        if (!_listBound) return;
        _listBound = false;
        ViewModel.TaskComposerFocusRequested -= ComposerFocusRequested;
        ViewModel.TaskSelectionRestored -= SelectionRestored;
        ((INotifyCollectionChanged)ViewModel.TaskGroups).CollectionChanged -= GroupsChanged;
        WinoStyle.AccentChanged -= AccentChanged;
        foreach (var group in _observedGroups) { group.CollectionChanged -= GroupChanged; ((INotifyPropertyChanged)group).PropertyChanged -= GroupPropertyChanged; }
        _observedGroups.Clear();
        _suggestionsPopover?.Close();
        _suggestionsPopover = null;
        _dueDatePopover?.Close();
        _dueDatePopover = null;
        ReleaseSearch();
    }

    private void AccentChanged(object? sender, EventArgs e) => OnUI(() => _title.TextColor = WinoStyle.Accent);

    private void ComposerFocusRequested(object? sender, Wino.Core.Domain.Models.TaskComposerFocusRequestedEventArgs e) => OnUI(() => _quickAdd.Focus());

    private void SubmitQuickAdd()
    {
        if (string.IsNullOrWhiteSpace(ViewModel.ComposerText)) return;
        Observe(ViewModel.AddTaskCommand.ExecuteAsync(null));
    }

    // ---- Groups → flat entries ----

    private void GroupsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnUI(() => { ObserveGroups(); ScheduleReload(); });
    private void GroupChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnUI(ScheduleReload);
    private void GroupPropertyChanged(object? sender, PropertyChangedEventArgs e) => OnUI(ScheduleReload);

    private void ObserveGroups()
    {
        foreach (var group in _observedGroups) { group.CollectionChanged -= GroupChanged; ((INotifyPropertyChanged)group).PropertyChanged -= GroupPropertyChanged; }
        _observedGroups.Clear();
        foreach (var group in ViewModel.TaskGroups)
        {
            group.CollectionChanged += GroupChanged;
            ((INotifyPropertyChanged)group).PropertyChanged += GroupPropertyChanged;
            _observedGroups.Add(group);
        }
    }

    private void ScheduleReload()
    {
        if (_reloadScheduled || _released) return;
        _reloadScheduled = true;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            _reloadScheduled = false;
            if (!_released && _listBound) RebuildEntries();
        });
    }

    private void RebuildEntries()
    {
        _entries.Clear();
        foreach (var group in _previewGroups ?? (IEnumerable<TaskGroup>)ViewModel.TaskGroups)
        {
            if (group.ShowHeader) _entries.Add(new ListEntry(group, null));
            foreach (var item in group) _entries.Add(new ListEntry(null, item));
        }
        _restoringSelection = true;
        try
        {
            _table.ReloadData();
            ApplySelection(ViewModel.SelectedTasks.ToList());
        }
        finally { _restoringSelection = false; }
    }

    private void SelectionRestored(object? sender, IReadOnlyList<TaskItemViewModel> selection) => OnUI(() =>
    {
        _restoringSelection = true;
        try { ApplySelection(selection); }
        finally { _restoringSelection = false; }
    });

    private void ApplySelection(IReadOnlyList<TaskItemViewModel> selection)
    {
        var indexes = new NSMutableIndexSet();
        for (int row = 0; row < _entries.Count; row++)
            if (_entries[row].Item is { } item && selection.Contains(item)) indexes.Add((nuint)row);
        if (indexes.Count == 0) _table.DeselectAll(null);
        else _table.SelectRows(indexes, false);
    }

    private void TableSelectionChanged()
    {
        if (_restoringSelection || _released) return;
        var selected = new List<TaskItemViewModel>();
        foreach (var row in _table.SelectedRows.ToArray())
            if (row < (nuint)_entries.Count && _entries[(int)row].Item is { } item) selected.Add(item);
        ViewModel.SetSelectedTasks(selected);
    }

    private ListEntry? EntryAt(nint row) => row >= 0 && row < _entries.Count ? _entries[(int)row] : null;

    private TaskItemViewModel? ItemForView(NSView view)
    {
        var row = _table.RowForView(view);
        return EntryAt(row)?.Item;
    }

    // ---- Cells ----

    private NSView CreateCell(NSTableView table, nint row)
    {
        var entry = _entries[(int)row];
        if (entry.Group is { } group)
        {
            var header = table.MakeView(WinoTaskGroupHeaderView.ReuseIdentifier, this) as WinoTaskGroupHeaderView ?? new WinoTaskGroupHeaderView();
            header.Apply(group.Key, group.HeaderCountText);
            return header;
        }

        var item = entry.Item!;
        if (table.MakeView(WinoTaskCardView.ReuseIdentifier, this) is not WinoTaskCardView card)
        {
            card = new WinoTaskCardView();
            card.CompletionToggled += (sender, _) => { if (sender is NSView view && ItemForView(view) is { } target) Observe(ViewModel.ToggleTaskCommand.ExecuteAsync(target)); };
            card.ImportanceToggled += (sender, _) => { if (sender is NSView view && ItemForView(view) is { } target) Observe(ViewModel.ToggleImportanceCommand.ExecuteAsync(target)); };
        }
        card.Apply(MapRow(item));
        card.Selected = table.IsRowSelected(row);
        PropertyChangedEventHandler handler = (_, _) => OnUI(() => { if (card.Subscription is not null && ItemForView(card) == item) card.Apply(MapRow(item)); });
        item.PropertyChanged += handler;
        card.Subscription = new ActionDisposable(() => item.PropertyChanged -= handler);
        return card;
    }

    private NSTableRowView CreateRowView(NSTableView table)
        => table.MakeView(WinoTaskTableRowView.ReuseIdentifier, this) as WinoTaskTableRowView ?? new WinoTaskTableRowView();

    private WinoTaskRowModel MapRow(TaskItemViewModel item) => new()
    {
        Title = item.Title ?? string.Empty,
        IsCompleted = item.IsCompleted,
        IsImportant = item.IsImportant,
        IsEditable = item.IsEditable,
        ListName = item.ShowListName && !string.IsNullOrEmpty(item.ListName) ? item.ListName : null,
        ListColor = ListColor(item),
        DueText = item.HasDueDate ? item.DueDisplayText : string.Empty,
        IsOverdue = item.IsOverdue,
        StepSummary = item.HasSteps ? item.StepSummaryText : string.Empty,
        IsInMyDay = item.IsInMyDay,
        HasNote = item.HasNote,
        MyDayText = Translator.ToDoPage_MyDay,
        ImportanceActionText = item.ImportanceActionText,
        CompletionActionText = item.CompletionActionText
    };

    private NSColor? ListColor(TaskItemViewModel item)
    {
        if (_previewColors.TryGetValue(item.Task.TaskListId, out var preview)) return preview;
        var list = ViewModel.TaskLists.FirstOrDefault(candidate => candidate.Id == item.Task.TaskListId);
        return WinoStyle.FromHexString(list?.ColorHex);
    }

    // ---- Debug preview: synthetic rows that are never persisted, for layout checks on an empty account ----

    private List<TaskGroup>? _previewGroups;
    private readonly Dictionary<Guid, NSColor> _previewColors = new();

#if DEBUG
    private void ShowPreview()
    {
        var today = DateTime.Now.Date;
        var releases = Guid.NewGuid(); var tasks = Guid.NewGuid(); var sprint = Guid.NewGuid();
        _previewColors[releases] = WinoStyle.Hex(0xE17055);
        _previewColors[tasks] = WinoStyle.Hex(0x4C5FD7);
        _previewColors[sprint] = WinoStyle.Hex(0x8764B8);
        TaskItemViewModel Item(string title, Guid list, string listName, DateTime? due = null, bool important = false, bool myDay = false, string? notes = null, bool done = false, params (string Title, bool Done)[] steps)
        {
            var task = new AccountTask { Title = title, TaskListId = list, DueDate = due, IsImportant = important, Notes = notes ?? string.Empty, IsCompleted = done,
                MyDayDateUtc = myDay ? DateTime.UtcNow.Date : null, CreatedAtUtc = DateTime.UtcNow.AddDays(-3) };
            task.Steps = steps.Select(step => new AccountTaskStep { TaskId = task.Id, Title = step.Title, IsCompleted = step.Done }).ToList();
            return new TaskItemViewModel(task, listName) { ShowListName = true };
        }
        var first = new TaskGroup("Burak Kaan Köse", Guid.NewGuid(), true)
        {
            Item("Review macOS sidebar parity PR", releases, "Wino releases", today, true, true, "Check compact rows and the colorful icon style too.", false,
                ("Account rows with avatars", true), ("Folder glyphs", true), ("Mode switcher card", false)),
            Item("Send Q4 roadmap notes to Ayşe", tasks, "Tasks", today.AddDays(-1), notes: "Attach the slide deck."),
            Item("Renew Apple developer membership", tasks, "Tasks", today.AddDays(2))
        };
        var second = new TaskGroup("Wino Team", Guid.NewGuid(), true)
        {
            Item("Prepare 2.2 release notes", sprint, "Sprint 42", important: true, steps: [("Draft", true), ("Screenshots", true), ("Review", false), ("Translate", false), ("Publish", false)]),
            Item("Book venue for team dinner", sprint, "Sprint 42", done: true)
        };
        _previewGroups = [first, second];
        _emptyState.Hidden = true;
        RebuildEntries();
    }
#endif

    // ---- Header popups ----

    /// <summary>Suggestions popover (Windows Flyout, 320 wide): adding pulls a task into today's My Day.</summary>
    private void ShowSuggestions()
    {
        _suggestionsPopover?.Close();
        ViewModel.OpenSuggestionsCommand.Execute(null);

        var content = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        var title = WinoStyle.Label(Translator.ToDoPage_Suggestions, WinoStyle.BodyStrong);
        var list = WinoLayout.VStack(1);
        list.WidthAnchor.ConstraintEqualTo(320).Active = true;
        var suggestions = ViewModel.Suggestions.ToList();
        if (suggestions.Count == 0)
        {
            list.AddArrangedSubview(WinoStyle.Label(Translator.ToDoPage_SuggestionsEmpty, WinoStyle.Body, WinoStyle.SecondaryText, 0));
        }
        foreach (var suggestion in suggestions.Take(12))
        {
            var row = new WinoDrawerActionRow(WinoIconGlyph.Add, suggestion.Title, WinoStyle.Accent, trailing: SuggestionCaption(suggestion)) { Plain = true };
            row.WidthAnchor.ConstraintEqualTo(320).Active = true;
            row.Clicked += (_, _) => { Observe(ViewModel.AddSuggestionToMyDayCommand.ExecuteAsync(suggestion)); _suggestionsPopover?.Close(); };
            list.AddArrangedSubview(row);
        }
        var stack = WinoLayout.VStack(8, title, list);
        WinoLayout.Fill(stack, content, 12);
        var controller = new NSViewController { View = content };
        _suggestionsPopover = new NSPopover { ContentViewController = controller, Behavior = NSPopoverBehavior.Transient };
        _suggestionsPopover.Show(_suggestionsButton.Bounds, _suggestionsButton, NSRectEdge.MaxYEdge);
        var observer = NSNotificationCenter.DefaultCenter.AddObserver(NSPopover.DidCloseNotification, _ => ViewModel.CloseSuggestionsCommand.Execute(null), _suggestionsPopover);
        Bindings.Own(new ActionDisposable(() => NSNotificationCenter.DefaultCenter.RemoveObserver(observer)));
    }

    private static string SuggestionCaption(TaskItemViewModel item)
        => string.Join(" · ", new[] { item.ListName, item.HasDueDate ? item.DueDisplayText : null }.Where(static part => !string.IsNullOrEmpty(part)));

    /// <summary>List overflow (Windows MenuFlyout): rename, print, email, delete.</summary>
    private void ShowListOptions()
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.AddItem(MenuItem(Translator.ToDoPage_RenameList, () => Observe(ViewModel.RenameSelectedListCommand.ExecuteAsync(null)), WinoIconGlyph.Rename, enabled: ViewModel.CanEditSelectedList));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(MenuItem(Translator.ToDoPage_PrintList, () => ViewModel.PrintListCommand.Execute(null), WinoIconGlyph.Print));
        menu.AddItem(MenuItem(Translator.ToDoPage_EmailList, () => ViewModel.EmailListCommand.Execute(null), WinoIconGlyph.Mail));
        if (ViewModel.CanDeleteSelectedList)
        {
            menu.AddItem(NSMenuItem.SeparatorItem);
            menu.AddItem(MenuItem(Translator.ToDoPage_DeleteList, () => Observe(ViewModel.DeleteListCommand.ExecuteAsync(null)), WinoIconGlyph.Delete, WinoToDoStyle.Critical));
        }
        menu.PopUpMenu(null, new CGPoint(0, -4), _moreButton);
    }

    /// <summary>Row context menu (Windows TaskContextRequested).</summary>
    private NSMenu? ContextMenuFor(nint row)
    {
        if (EntryAt(row)?.Item is not { } task) return null;
        if (!_table.IsRowSelected(row)) _table.SelectRow(row, false);
        var editable = task.IsEditable;
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.AddItem(MenuItem(task.MyDayActionText, () => Observe(ViewModel.ToggleMyDayCommand.ExecuteAsync(task)), WinoIconGlyph.WeatherSunny, enabled: editable));
        menu.AddItem(MenuItem(task.ImportanceActionText, () => Observe(ViewModel.ToggleImportanceCommand.ExecuteAsync(task)), WinoIconGlyph.Star, enabled: editable));
        menu.AddItem(MenuItem(task.CompletionActionText, () => Observe(ViewModel.ToggleTaskCommand.ExecuteAsync(task)), WinoIconGlyph.Checkmark, enabled: editable));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(MenuItem(Translator.ToDoPage_DueToday, () => Observe(ViewModel.SetTaskDueDateAsync(task, DateTime.Now.Date)), WinoIconGlyph.CalendarToday, enabled: editable));
        menu.AddItem(MenuItem(Translator.ToDoPage_DueTomorrow, () => Observe(ViewModel.SetTaskDueDateAsync(task, DateTime.Now.Date.AddDays(1))), WinoIconGlyph.Calendar, enabled: editable));
        menu.AddItem(MenuItem(Translator.ToDoPage_DuePresetPickDate + "…", () => ShowDueDatePicker(task, row), WinoIconGlyph.Calendar, enabled: editable));
        menu.AddItem(MenuItem(Translator.ToDoPage_RemoveDueDate, () => Observe(ViewModel.SetTaskDueDateAsync(task, null)), WinoIconGlyph.Calendar, enabled: editable && task.HasDueDate));
        var destinations = ViewModel.TaskLists.Where(list => !list.IsReadOnly && list.Id != task.Task.TaskListId &&
            list.MailAccountId == task.Task.MailAccountId && list.SourceKind == task.Task.SourceKind).ToList();
        if (destinations.Count > 0)
        {
            menu.AddItem(NSMenuItem.SeparatorItem);
            var moveMenu = new NSMenu { AutoEnablesItems = false };
            foreach (var destination in destinations)
                moveMenu.AddItem(MenuItem(destination.Title, () => Observe(ViewModel.MoveTaskAsync(task, destination)), WinoIconGlyph.Folder));
            menu.AddItem(new NSMenuItem(Translator.ToDoPage_MoveTaskTo) { Submenu = moveMenu, Enabled = editable, Image = WinoIcons.Image(WinoIconGlyph.Move, 14) });
        }
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(MenuItem(Translator.ToDoPage_DeleteTask, () => Observe(ViewModel.DeleteTaskCommand.ExecuteAsync(task)), WinoIconGlyph.Delete, WinoToDoStyle.Critical, editable));
        return menu;
    }

    /// <summary>
    /// "Pick a date" (Windows TaskDueDatePickerHost DatePickerFlyout): a graphical date picker in a transient
    /// popover beside the row. OK (Return) sets the due date; Cancel, Escape or clicking outside leaves it.
    /// </summary>
    private void ShowDueDatePicker(TaskItemViewModel task, nint row)
    {
        _dueDatePopover?.Close();
        var initial = (task.DueDate ?? DateTime.Now).Date;
        var picker = new NSDatePicker
        {
            DatePickerStyle = NSDatePickerStyle.ClockAndCalendar,
            DatePickerElements = NSDatePickerElementFlags.YearMonthDate,
            Calendar = NSCalendar.CurrentCalendar,
            TimeZone = NSTimeZone.LocalTimeZone,
            DateValue = (NSDate)DateTime.SpecifyKind(initial, DateTimeKind.Local),
            Bezeled = false,
            DrawsBackground = false,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoAccessibility.Label(picker, Translator.ToDoPage_DuePresetPickDate);

        NSPopover? popover = null;
        var cancel = new NSButton { Title = Translator.Buttons_Cancel, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\u001b" };
        cancel.Activated += (_, _) => popover?.Close();
        var ok = new NSButton { Title = Translator.Buttons_OK, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\r" };
        ok.Activated += (_, _) =>
        {
            var picked = ((DateTime)picker.DateValue).ToLocalTime().Date;
            popover?.Close();
            CommitPickedDueDate(task, picked);
        };
        var buttons = WinoLayout.HStack(8, WinoLayout.Spacer(), cancel, ok);
        var stack = WinoLayout.VStack(12, picker, buttons);
        stack.Alignment = NSLayoutAttribute.CenterX;
        var content = new NSView();
        WinoLayout.Fill(stack, content, 12, 12, 12, 12);
        buttons.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        var controller = new NSViewController { View = content };

        popover = _dueDatePopover = new NSPopover { ContentViewController = controller, Behavior = NSPopoverBehavior.Transient, Animates = true };
        var anchor = row >= 0 && row < _table.RowCount ? _table.RectForRow(row) : _table.VisibleRect();
        popover.Show(anchor, _table, NSRectEdge.MaxXEdge);
        content.Window?.MakeFirstResponder(picker);
    }

    private void CommitPickedDueDate(TaskItemViewModel task, DateTime date)
    {
        // The list may have reloaded while the popover was open; use the current item for the task.
        var taskId = task.Task.Id;
        var current = _entries.Select(entry => entry.Item).FirstOrDefault(item => item?.Task.Id == taskId) ?? task;
        Observe(ViewModel.SetTaskDueDateAsync(current, date.Date));
    }

    private sealed class TaskTableDataSource(ToDoPageViewController owner) : NSTableViewDataSource
    {
        public override nint GetRowCount(NSTableView tableView) => owner._entries.Count;
    }

    private sealed class TaskTableDelegate(ToDoPageViewController owner) : NSTableViewDelegate
    {
        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row) => owner.CreateCell(tableView, row);
        public override NSTableRowView CoreGetRowView(NSTableView tableView, nint row) => owner.CreateRowView(tableView);
        public override nfloat GetRowHeight(NSTableView tableView, nint row)
            => (nfloat)(owner.EntryAt(row)?.Group is not null ? WinoToDoStyle.GroupHeaderHeight : WinoToDoStyle.TaskRowHeight);
        public override bool IsGroupRow(NSTableView tableView, nint row) => owner.EntryAt(row)?.Group is not null;
        public override bool ShouldSelectRow(NSTableView tableView, nint row) => owner.EntryAt(row)?.Item is not null;
        public override void SelectionDidChange(NSNotification notification) => owner.TableSelectionChanged();
    }

    /// <summary>
    /// Right-click opens the task menu. The Delete key is a configurable shortcut, routed by the app's
    /// mode shortcut router (AppDelegate.Shortcuts) to ToDoPageViewModel.KeyboardShortcutHook.
    /// </summary>
    private sealed class TaskTableView(ToDoPageViewController owner) : NSTableView
    {
        public override NSMenu? MenuForEvent(NSEvent theEvent)
        {
            var point = ConvertPointFromView(theEvent.LocationInWindow, null);
            return owner.ContextMenuFor(GetRow(point));
        }
    }
}
