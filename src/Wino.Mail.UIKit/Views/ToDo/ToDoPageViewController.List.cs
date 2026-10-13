using System.Collections.Specialized;
using System.ComponentModel;
using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Mail.Controls.Core.ToDo;
using Wino.Mail.Controls.UIKit.ToDo;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.ToDo;

public abstract partial class ToDoPageViewController
{
    private readonly List<TaskGroup> _groups = new();
    private readonly List<TaskGroup> _observedGroups = new();
    private readonly UITableView _table = new(CGRect.Empty, UITableViewStyle.Plain);
    private readonly UIRefreshControl _refresh = new();
    private bool _reloadScheduled;
    private bool _restoringSelection;
    private bool _listBound;

    protected UITableView Table => _table;

    private void BuildTable()
    {
        _table.TranslatesAutoresizingMaskIntoConstraints = false;
        _table.BackgroundColor = UIColor.Clear;
        _table.SeparatorStyle = UITableViewCellSeparatorStyle.None;
        _table.SectionHeaderTopPadding = 0;
        _table.KeyboardDismissMode = UIScrollViewKeyboardDismissMode.OnDrag;
        _table.RegisterClassForCellReuse(typeof(WinoTaskRowCell), WinoTaskRowCell.ReuseIdentifier);
        _table.RegisterClassForHeaderFooterViewReuse(typeof(WinoTaskGroupHeaderView), WinoTaskGroupHeaderView.ReuseIdentifier);
        _table.DataSource = new TaskSource(this);
        _table.Delegate = new TaskDelegate(this);
        _refresh.ValueChanged += (_, _) => Synchronize();
        _table.RefreshControl = _refresh;
        WinoAccessibility.Label(_table, Translator.ToDoPage_Tasks);
    }

    private void BindList()
    {
        if (_listBound) return;
        _listBound = true;
        ViewModel.TaskSelectionRestored += SelectionRestored;
        ((INotifyCollectionChanged)ViewModel.TaskGroups).CollectionChanged += GroupsChanged;
        ObserveGroups();
        RebuildGroups();
    }

    private void ReleaseList()
    {
        if (!_listBound) return;
        _listBound = false;
        ViewModel.TaskSelectionRestored -= SelectionRestored;
        ((INotifyCollectionChanged)ViewModel.TaskGroups).CollectionChanged -= GroupsChanged;
        foreach (var group in _observedGroups)
        {
            group.CollectionChanged -= GroupChanged;
            ((INotifyPropertyChanged)group).PropertyChanged -= GroupPropertyChanged;
        }
        _observedGroups.Clear();
    }

    private void Synchronize()
    {
        if (ViewModel.SynchronizeCommand.CanExecute(null)) ViewModel.SynchronizeCommand.Execute(null);
        // Synchronization runs in the background; the list follows its completion message.
        NSTimer.CreateScheduledTimer(0.8, _ => { if (_refresh.Refreshing) _refresh.EndRefreshing(); });
    }

    private void GroupsChanged(object? sender, NotifyCollectionChangedEventArgs args) => OnUI(() => { ObserveGroups(); ScheduleReload(); });
    private void GroupChanged(object? sender, NotifyCollectionChangedEventArgs args) => OnUI(ScheduleReload);
    private void GroupPropertyChanged(object? sender, PropertyChangedEventArgs args) => OnUI(ScheduleReload);

    private void ObserveGroups()
    {
        foreach (var group in _observedGroups)
        {
            group.CollectionChanged -= GroupChanged;
            ((INotifyPropertyChanged)group).PropertyChanged -= GroupPropertyChanged;
        }
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
        if (_reloadScheduled || IsReleased) return;
        _reloadScheduled = true;
        NSRunLoop.Main.BeginInvokeOnMainThread(() =>
        {
            _reloadScheduled = false;
            if (!IsReleased && _listBound) RebuildGroups();
        });
    }

    private void RebuildGroups()
    {
        _groups.Clear();
        _groups.AddRange(ViewModel.TaskGroups);
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
        foreach (var path in _table.IndexPathsForSelectedRows ?? [])
            if (TaskAt(path) is not { } item || !selection.Contains(item)) _table.DeselectRow(path, false);
        foreach (var item in selection)
            if (IndexPathOf(item) is { } path) _table.SelectRow(path, false, UITableViewScrollPosition.None);
    }

    /// <summary>Clears the table's visible selection (the iPhone, back from the detail).</summary>
    protected void DeselectVisibleRows()
    {
        foreach (var path in _table.IndexPathsForSelectedRows ?? []) _table.DeselectRow(path, true);
    }

    private TaskItemViewModel? TaskAt(NSIndexPath indexPath)
    {
        if (indexPath.Section < 0 || indexPath.Section >= _groups.Count) return null;
        var group = _groups[(int)indexPath.Section];
        return indexPath.Row >= 0 && indexPath.Row < group.Count ? group[(int)indexPath.Row] : null;
    }

    private NSIndexPath? IndexPathOf(TaskItemViewModel item)
    {
        for (int section = 0; section < _groups.Count; section++)
        {
            var index = _groups[section].IndexOf(item);
            if (index >= 0) return NSIndexPath.FromRowSection(index, section);
        }
        return null;
    }

    private void RowTapped(NSIndexPath indexPath)
    {
        if (_restoringSelection || TaskAt(indexPath) is not { } task) return;
        ViewModel.SetSelectedTasks([task]);
        TaskActivated(task);
    }

    private void BindCell(WinoTaskRowCell cell, TaskItemViewModel item)
    {
        cell.Apply(MapRow(item));
        PropertyChangedEventHandler handler = (_, _) => OnUI(() => { if (cell.Subscription is not null) cell.Apply(MapRow(item)); });
        item.PropertyChanged += handler;
        cell.Subscription = new ActionDisposable(() => item.PropertyChanged -= handler);
    }

    private void WireCell(WinoTaskRowCell cell)
    {
        cell.CompletionToggled += (sender, _) => { if (sender is UITableViewCell row && _table.IndexPathForCell(row) is { } path && TaskAt(path) is { } task) Observe(ViewModel.ToggleTaskCommand.ExecuteAsync(task)); };
        cell.ImportanceToggled += (sender, _) => { if (sender is UITableViewCell row && _table.IndexPathForCell(row) is { } path && TaskAt(path) is { } task) Observe(ViewModel.ToggleImportanceCommand.ExecuteAsync(task)); };
    }

    private WinoTaskRowModel MapRow(TaskItemViewModel item) => new()
    {
        Title = item.Title ?? string.Empty,
        IsCompleted = item.IsCompleted,
        IsImportant = item.IsImportant,
        IsEditable = item.IsEditable,
        ListName = item.ShowListName && !string.IsNullOrEmpty(item.ListName) ? item.ListName : null,
        ListColorHex = ViewModel.TaskLists.FirstOrDefault(list => list.Id == item.Task.TaskListId)?.ColorHex,
        DueText = item.HasDueDate ? item.DueDisplayText : string.Empty,
        IsOverdue = item.IsOverdue,
        StepSummary = item.HasSteps ? item.StepSummaryText : string.Empty,
        IsInMyDay = item.IsInMyDay,
        HasNote = item.HasNote,
        MyDayText = Translator.ToDoPage_MyDay,
        ImportanceActionText = item.ImportanceActionText,
        CompletionActionText = item.CompletionActionText
    };

    private sealed class TaskSource(ToDoPageViewController owner) : UITableViewDataSource
    {
        private readonly HashSet<IntPtr> _wired = new();

        public override nint NumberOfSections(UITableView tableView) => owner._groups.Count;

        public override nint RowsInSection(UITableView tableView, nint section)
            => section >= 0 && section < owner._groups.Count ? owner._groups[(int)section].Count : 0;

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var cell = (WinoTaskRowCell)tableView.DequeueReusableCell(WinoTaskRowCell.ReuseIdentifier, indexPath);
            if (_wired.Add(cell.Handle)) owner.WireCell(cell);
            if (owner.TaskAt(indexPath) is { } item) owner.BindCell(cell, item);
            return cell;
        }
    }

    private sealed class TaskDelegate(ToDoPageViewController owner) : UITableViewDelegate
    {
        public override nfloat GetHeightForRow(UITableView tableView, NSIndexPath indexPath) => (nfloat)WinoToDoStyle.TaskRowHeight;

        public override nfloat GetHeightForHeader(UITableView tableView, nint section)
            => section >= 0 && section < owner._groups.Count && owner._groups[(int)section].ShowHeader ? (nfloat)WinoToDoStyle.GroupHeaderHeight : 0;

        public override nfloat GetHeightForFooter(UITableView tableView, nint section) => 0;

        public override UIView? GetViewForHeader(UITableView tableView, nint section)
        {
            if (section < 0 || section >= owner._groups.Count || !owner._groups[(int)section].ShowHeader) return null;
            if (tableView.DequeueReusableHeaderFooterView(WinoTaskGroupHeaderView.ReuseIdentifier) is not WinoTaskGroupHeaderView header)
                header = new WinoTaskGroupHeaderView();
            var group = owner._groups[(int)section];
            header.Apply(group.Key, group.HeaderCountText);
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
            if (cell is WinoTaskRowCell row) row.Subscription = null;
        }

        public override void WillDisplay(UITableView tableView, UITableViewCell cell, NSIndexPath indexPath)
        {
            if (cell is WinoTaskRowCell { Subscription: null } row && owner.TaskAt(indexPath) is { } item) owner.BindCell(row, item);
        }
    }
}
