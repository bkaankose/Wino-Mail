using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Views.Common;
using Wino.Mail.Linux.Views.Shell;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Lists;

namespace Wino.Mail.Linux.Views.ToDo;

/// <summary>Keyboard movement for the shell: the task list and the drawer as panes, Enter into the drawer, Alt+Down and Alt+Up through the tasks, Ctrl+D to complete.</summary>
internal sealed partial class ToDoPageView : IPaneFocus, IDetailPane, IItemNavigation, IKeyActionTarget
{
    private Action? _detailRequested;
    private bool _activateConnected;

    public bool HasDetail => _layout.ShowsEnd && ViewModel.SelectedTasks.Count > 0;

    public Gtk.Widget? DetailRoot => _drawerHost;

    public Action? DetailRequested
    {
        set
        {
            _detailRequested = value;
            if (_activateConnected || IsReleased) return;
            _activateConnected = true;
            var self = _self;
            var list = _list;
            Bindings.Own(GtkSignals.Connect<Gtk.ListView, Gtk.ListView.ActivateSignalArgs>(
                h => list.OnActivate += h, h => list.OnActivate -= h, (_, _) => self.Value?._detailRequested?.Invoke()));
        }
    }

    public bool FocusPrimary()
    {
        if (IsReleased || !_list.GetMapped()) return false;
        var selected = ListViews.SelectedPositions(_selection);
        if (selected.Length > 0 || RowCount > 0)
        {
            _list.ScrollTo(selected.Length > 0 ? selected[0] : 0, Gtk.ListScrollFlags.Focus, null);
            if (_list.GetRoot() is Gtk.Window window && ShellView.Contains(_list, window.GetFocus())) return true;
        }
        return _list.GrabFocus();
    }

    public bool FocusDetail() => !IsReleased && _drawerHost.GetMapped() && _drawerHost.ChildFocus(Gtk.DirectionType.TabForward);

    public bool CloseDetail()
    {
        if (IsReleased || _layout.State != EndPaneState.Swapped) return false;
        _selection.UnselectAll();
        return true;
    }

    public bool MoveItem(int delta)
    {
        if (IsReleased || RowCount == 0) return false;
        var selected = ListViews.SelectedPositions(_selection);
        long start = selected.Length == 1 ? selected[0] : delta > 0 ? -1 : RowCount;
        var target = start + delta;
        if (target < 0 || target >= RowCount) return true;
        ListViews.Select(_selection, [(uint)target]);
        ListViews.ScrollTo(_list, (uint)target);
        return true;
    }

    public bool TryHandleKeyAction(string action)
    {
        if (action != LinuxKeymap.TaskComplete || IsReleased) return false;
        var tasks = ViewModel.SelectedTasks;
        if (tasks.Count == 1 && tasks[0] is { } task)
        {
            if (ViewModel.ToggleTaskCommand.CanExecute(task)) Observe(ViewModel.ToggleTaskCommand.ExecuteAsync(task));
            return true;
        }
        if (tasks.Count == 0) return false;
        if (ViewModel.CompleteSelectedTasksCommand.CanExecute(null)) Observe(ViewModel.CompleteSelectedTasksCommand.ExecuteAsync(null));
        return true;
    }
}
