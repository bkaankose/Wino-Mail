using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Wino.Core.Domain;
using Wino.Core.Domain.Models;
using Wino.Mail.Controls.Linux.ToDo;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Lists;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.ToDo;

/// <summary>
/// The task list and quick add (macOS ToDoPageViewController.List, Windows ToDoPage task template): the ViewModel's
/// groups flattened into one row array, account groups as GTK sections with headers (a named list is one headerless
/// group), the multi-selection mirrored into the ViewModel, and the quick add card. Rebuilds are coalesced to one per
/// main-loop turn.
/// </summary>
internal sealed partial class ToDoPageView
{
    private readonly Dictionary<TaskItemViewModel, int> _sectionOf = new(ReferenceEqualityComparer.Instance);
    private readonly List<TaskGroup> _groups = [];
    private readonly List<TaskGroup> _observedGroups = [];
    private readonly Dictionary<TaskGroup, Gtk.Widget> _headerWidgets = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Guid, string?> _listColors = [];
    private ObservableListModel<TaskItemViewModel> _model;
    private ListSections<TaskItemViewModel> _sections;
    private Gtk.MultiSelection _selection;
    private WinoCellFactory<TaskCell, TaskItemViewModel> _cells;
    private WinoHeaderFactory<TaskItemViewModel> _headers;
    private Gtk.ListView _list;
    private Gtk.ScrolledWindow _scroller;
    private WinoQuickAdd _quickAdd;
    private MainLoop.Coalescer _reflow;
    private bool _showsHeaders;
    private bool _restoringSelection;

    internal Gtk.ListView List => _list;

    internal Gtk.MultiSelection Selection => _selection;

    internal WinoCellFactory<TaskCell, TaskItemViewModel> Cells => _cells;

    internal ListSections<TaskItemViewModel> Sections => _sections;

    internal WinoQuickAdd QuickAdd => _quickAdd;

    internal Gtk.ScrolledWindow Scroller => _scroller;

    internal int RowCount => _model.Count;

    internal int SectionCount => _groups.Count;

    internal bool ShowsHeaders => _showsHeaders;

    /// <summary>The rows in screen order.</summary>
    internal TaskItemViewModel[] VisibleTasks
    {
        get
        {
            var rows = new TaskItemViewModel[_model.Count];
            for (uint position = 0; position < rows.Length; position++)
                if (_sections.ItemAt(position) is { } item) rows[position] = item;
            return rows;
        }
    }

    partial void TaskContextCellSetup(TaskCell cell);
    partial void TaskContextCellTeardown(TaskCell cell);
    partial void TaskDragCellSetup(TaskCell cell);
    partial void TaskDragCellTeardown(TaskCell cell);

    [MemberNotNull(nameof(_model), nameof(_sections), nameof(_selection), nameof(_cells), nameof(_headers), nameof(_list),
        nameof(_scroller), nameof(_quickAdd), nameof(_reflow))]
    partial void BuildList(Adw.Bin listHost, Adw.Bin quickAddHost)
    {
        var self = _self;
        _model = Bindings.Own(new ObservableListModel<TaskItemViewModel>(Dispatcher, ReportError));
        _sections = Bindings.Own(new ListSections<TaskItemViewModel>(_model.Store,
            (a, b) => self.Value is { } view ? view.SectionOf(a).CompareTo(view.SectionOf(b)) : 0, null, ReportError));
        _selection = Gtk.MultiSelection.New(_sections.Model);
        _cells = Bindings.Own(new WinoCellFactory<TaskCell, TaskItemViewModel>(
            () => new TaskCell(item => self.Value?.ListColor(item), Dispatcher,
                item => self.Value?.ToggleCompletion(item), item => self.Value?.ToggleImportance(item)), ReportError)
        {
            Setup = (cell, _) =>
            {
                if (self.Value is not { } view) return;
                view.TaskContextCellSetup(cell);
                view.TaskDragCellSetup(cell);
            },
            Teardown = cell =>
            {
                if (self.Value is { } view)
                {
                    view.TaskDragCellTeardown(cell);
                    view.TaskContextCellTeardown(cell);
                }
                cell.Card.Dispose();
            },
        });
        _headers = Bindings.Own(new WinoHeaderFactory<TaskItemViewModel>(WinoTaskGroupHeader.Create,
            (widget, item, _) => self.Value?.BindHeader(widget, item), ReportError));

        _list = Gtk.ListView.New(_selection, _cells.Factory);
        _list.SetSingleClickActivate(false);
        _list.SetShowSeparators(false);
        _list.SetEnableRubberband(false);
        _list.AddCssClass(WinoCssClass.TaskList);
        WinoA11y.Label(_list, Translator.ToDoPage_Tasks);

        _scroller = Gtk.ScrolledWindow.New();
        _scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        _scroller.SetChild(_list);
        _scroller.SetVexpand(true);
        _scroller.SetHexpand(true);
        listHost.SetChild(_scroller);

        _quickAdd = Bindings.Own(new WinoQuickAdd(Translator.ToDoPage_AddTask));
        quickAddHost.SetChild(_quickAdd.Root);
        _quickAdd.Submitted += () => self.Value?.SubmitQuickAdd();
        _quickAdd.TextChanged += () =>
        {
            if (self.Value is { } view) view.ViewModel.ComposerText = view._quickAdd.Text;
        };
        _quickAdd.FocusChanged += () => self.Value?.QuickAddFocusChanged();

        _reflow = Bindings.Own(new MainLoop.Coalescer(() => self.Value?.Flatten()));
        Bindings.Own(ListViews.KeepSelection(_model, _selection, item => self.Value?._sections.PositionOf(item)));
        var selection = _selection;
        Bindings.Own(GtkSignals.Connect<Gtk.SelectionModel, Gtk.SelectionModel.SelectionChangedSignalArgs>(
            h => selection.OnSelectionChanged += h, h => selection.OnSelectionChanged -= h,
            (_, _) => self.Value?.SelectionChanged()));
    }

    partial void BindList()
    {
        var self = _self;
        var groups = (INotifyCollectionChanged)ViewModel.TaskGroups;
        Bind.BindCollection(groups, () =>
        {
            ObserveGroups();
            _reflow.Request();
        });
        Bindings.Own(new ActionDisposable(UnobserveGroups));
        Bind.BindCollection(ViewModel.TaskLists, () =>
        {
            _listColors.Clear();
            foreach (var list in ViewModel.TaskLists)
                if (list is not null) _listColors[list.Id] = list.ColorHex;
            _cells.ForEachBound(static (cell, _) => cell.Refresh());
        });
        Bind.Bind(nameof(ToDoPageViewModel.ComposerPlaceholder), static vm => vm.ComposerPlaceholder, value => _quickAdd.Placeholder = value ?? string.Empty);
        Bind.Bind(nameof(ToDoPageViewModel.ComposerText), static vm => vm.ComposerText, value => _quickAdd.Text = value ?? string.Empty);
        Bind.Bind(nameof(ToDoPageViewModel.CanCreateTask), static vm => vm.CanCreateTask, value => _quickAdd.Enabled = value);
        Bind.Bind(nameof(ToDoPageViewModel.IsComposerExpanded), static vm => vm.IsComposerExpanded, value => _quickAdd.ShowButton = value);
        Bind.Bind(nameof(ToDoPageViewModel.SelectedTask), static vm => vm.SelectedTask, _ => ApplySelection(ViewModel.SelectedTasks.ToArray()));

        EventHandler<IReadOnlyList<TaskItemViewModel>> restored = (_, tasks) =>
        {
            TaskItemViewModel[] copy = [.. tasks];
            _ = Dispatcher.ExecuteOnUIThread(() => self.Value?.ApplySelection(copy));
        };
        ViewModel.TaskSelectionRestored += restored;
        Bindings.Own(new ActionDisposable(() => ViewModel.TaskSelectionRestored -= restored));
        EventHandler<TaskComposerFocusRequestedEventArgs> focus = (_, _) => _ = Dispatcher.ExecuteOnUIThread(() => self.Value?._quickAdd.Focus());
        ViewModel.TaskComposerFocusRequested += focus;
        Bindings.Own(new ActionDisposable(() => ViewModel.TaskComposerFocusRequested -= focus));
    }

    /// <summary>Flushes a pending rebuild, then selects and scrolls to the task; false when it is not listed.</summary>
    internal bool RevealTask(Guid taskId)
    {
        if (IsReleased) return false;
        Flatten();
        for (uint position = 0; position < _model.Count; position++)
        {
            if (_sections.ItemAt(position) is not { } item || item.Task?.Id != taskId) continue;
            ListViews.Select(_selection, [position]);
            ListViews.ScrollTo(_list, position);
            return true;
        }
        return false;
    }

    private void ObserveGroups()
    {
        UnobserveGroups();
        foreach (var group in ViewModel.TaskGroups)
        {
            if (group is null) continue;
            group.CollectionChanged += GroupChanged;
            ((INotifyPropertyChanged)group).PropertyChanged += GroupPropertyChanged;
            _observedGroups.Add(group);
        }
    }

    private void UnobserveGroups()
    {
        foreach (var group in _observedGroups)
        {
            group.CollectionChanged -= GroupChanged;
            ((INotifyPropertyChanged)group).PropertyChanged -= GroupPropertyChanged;
        }
        _observedGroups.Clear();
    }

    private void GroupChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        var self = _self;
        _ = Dispatcher.ExecuteOnUIThread(() => self.Value?._reflow.Request());
    }

    private void GroupPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(TaskGroup.Key) || sender is not TaskGroup group) return;
        var self = _self;
        _ = Dispatcher.ExecuteOnUIThread(() =>
        {
            if (self.Value is { } view && view._headerWidgets.TryGetValue(group, out var widget) && widget.GetMapped())
                WinoTaskGroupHeader.Apply(widget, group.Key, group.HeaderCountText);
        });
    }

    private int SectionOf(TaskItemViewModel item) => _sectionOf.TryGetValue(item, out var index) ? index : int.MaxValue;

    private string? ListColor(TaskItemViewModel item)
        => item.Task is { } task && _listColors.TryGetValue(task.TaskListId, out var hex) ? hex : null;

    private void Flatten()
    {
        if (IsReleased) return;
        var rows = new List<TaskItemViewModel>(Math.Max(_model.Count, 16));
        _sectionOf.Clear();
        _groups.Clear();
        _headerWidgets.Clear();
        var headers = false;
        foreach (var group in ViewModel.TaskGroups)
        {
            if (group is null || group.Count == 0) continue;
            _groups.Add(group);
            headers |= group.ShowHeader;
            var section = _groups.Count - 1;
            foreach (var item in group)
            {
                if (item is null || _sectionOf.ContainsKey(item)) continue;
                _sectionOf[item] = section;
                rows.Add(item);
            }
        }
        if (headers != _showsHeaders)
        {
            _showsHeaders = headers;
            _list.SetHeaderFactory(headers ? _headers.Factory : null);
        }
        _model.SetItems(rows);
        _sections.Invalidate();
        ApplySelection(ViewModel.SelectedTasks.ToArray());
    }

    private void BindHeader(Gtk.Widget widget, TaskItemViewModel item)
    {
        if (!_sectionOf.TryGetValue(item, out var section) || section < 0 || section >= _groups.Count) return;
        var group = _groups[section];
        _headerWidgets[group] = widget;
        WinoTaskGroupHeader.Apply(widget, group.ShowHeader ? group.Key : string.Empty, group.ShowHeader ? group.HeaderCountText : string.Empty);
    }

    private void SelectionChanged()
    {
        if (_model.IsApplying || _restoringSelection || IsReleased) return;
        ViewModel.SetSelectedTasks(ListViews.SelectedValues<TaskItemViewModel>(_selection));
    }

    internal void ApplySelection(TaskItemViewModel[] tasks)
    {
        if (IsReleased) return;
        var positions = new List<uint>(tasks.Length);
        foreach (var task in tasks)
        {
            if (task is null) continue;
            if (PositionOf(task) is { } position) positions.Add(position);
        }
        var current = ListViews.SelectedPositions(_selection);
        if (current.Length == positions.Count && current.All(positions.Contains)) return;
        _restoringSelection = true;
        try
        {
            if (positions.Count == 0) _selection.UnselectAll();
            else ListViews.Select(_selection, [.. positions]);
        }
        finally { _restoringSelection = false; }
    }

    internal uint? PositionOf(TaskItemViewModel task)
    {
        if (_sections.PositionOf(task) is { } position) return position;
        for (uint index = 0; index < _model.Count; index++)
        {
            if (_sections.ItemAt(index) is { } item && item.Task?.Id == task.Task?.Id && item.Task is not null) return index;
        }
        return null;
    }

    private void ToggleCompletion(TaskItemViewModel item)
    {
        if (IsReleased) return;
        var command = ViewModel.ToggleTaskCommand;
        if (command.CanExecute(item)) Observe(command.ExecuteAsync(item));
    }

    private void ToggleImportance(TaskItemViewModel item)
    {
        if (IsReleased) return;
        var command = ViewModel.ToggleImportanceCommand;
        if (command.CanExecute(item)) Observe(command.ExecuteAsync(item));
    }

    internal void SubmitQuickAdd()
    {
        if (IsReleased || string.IsNullOrWhiteSpace(ViewModel.ComposerText)) return;
        var command = ViewModel.AddTaskCommand;
        if (command.CanExecute(null)) Observe(command.ExecuteAsync(null));
    }

    private void QuickAddFocusChanged()
    {
        if (IsReleased) return;
        if (_quickAdd.IsFocused) ViewModel.IsComposerExpanded = true;
        else if (string.IsNullOrWhiteSpace(ViewModel.ComposerText)) ViewModel.IsComposerExpanded = false;
    }
}
