using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.MacOS.Views.ToDo;

/// <summary>
/// Manual sort (Windows ToDoPage TaskListView drag handlers): task rows of a named list drag to a new
/// position. Dragging a selected row moves the whole selection.
/// </summary>
public sealed partial class ToDoPageViewController
{
    private const string TaskReorderPasteboardType = "com.winomail.todo-task-row";
    private readonly string _reorderToken = Guid.NewGuid().ToString("N");
    private List<TaskItemViewModel> _draggedTasks = [];

    private void ConfigureTaskReorder()
    {
        _table.RegisterForDraggedTypes([TaskReorderPasteboardType]);
        _table.SetDraggingSourceOperationMask(NSDragOperation.Move, true);
        _table.SetDraggingSourceOperationMask(NSDragOperation.None, false);
        _table.DraggingDestinationFeedbackStyle = NSTableViewDraggingDestinationFeedbackStyle.Regular;
    }

    private INSPasteboardWriting? TaskPasteboardWriterForRow(nint row)
    {
        if (_previewGroups is not null || !ViewModel.CanReorderTasks || EntryAt(row)?.Item is null) return null;
        var item = new NSPasteboardItem();
        item.SetStringForType(_reorderToken, TaskReorderPasteboardType);
        return item;
    }

    private void TaskDragWillBegin(NSIndexSet rows)
    {
        _draggedTasks = rows.ToArray()
            .Where(index => index < (nuint)_entries.Count)
            .Select(index => _entries[(int)index].Item)
            .OfType<TaskItemViewModel>()
            .ToList();
    }

    private void TaskDragEnded() => _draggedTasks = [];

    private bool IsOwnTaskDrag(INSDraggingInfo info)
        => _draggedTasks.Count > 0 && ViewModel.CanReorderTasks
           && info.DraggingPasteboard.GetStringForType(TaskReorderPasteboardType) == _reorderToken;

    private NSDragOperation ValidateTaskDrop(NSTableView table, INSDraggingInfo info, nint row, NSTableViewDropOperation operation)
    {
        if (!IsOwnTaskDrag(info)) return NSDragOperation.None;
        if (operation == NSTableViewDropOperation.On) table.SetDropRowDropOperation(row, NSTableViewDropOperation.Above);
        return ResolveTaskDrop(row) is null ? NSDragOperation.None : NSDragOperation.Move;
    }

    private bool AcceptTaskDrop(INSDraggingInfo info, nint row)
    {
        if (!IsOwnTaskDrag(info) || ResolveTaskDrop(row) is not { } drop) return false;
        var moved = _draggedTasks.ToList();
        Observe(ViewModel.ReorderTasksAsync(moved, drop.Target, drop.InsertAfter));
        return true;
    }

    /// <summary>
    /// Maps an "above row" drop to a neighbour task. A gap on a group header or at the end attaches to
    /// the task before it; the dragged tasks themselves are skipped since they cannot be the anchor.
    /// </summary>
    private (TaskItemViewModel Target, bool InsertAfter)? ResolveTaskDrop(nint row)
    {
        var gap = (int)Math.Clamp(row, 0, _entries.Count);
        for (var index = gap - 1; index >= 0; index--)
        {
            if (_entries[index].Item is not { } before) continue;
            if (_draggedTasks.Contains(before)) break;
            return (before, true);
        }
        for (var index = gap; index < _entries.Count; index++)
        {
            if (_entries[index].Item is not { } after) continue;
            if (_draggedTasks.Contains(after)) continue;
            return (after, false);
        }
        for (var index = gap - 1; index >= 0; index--)
        {
            if (_entries[index].Item is { } before && !_draggedTasks.Contains(before)) return (before, true);
        }
        return null;
    }

#if DEBUG
    private void RegisterReorderDebugCommands()
    {
        // "todo-rows" prints each row with its screen rect (top-left origin) for driving real drags.
        Infrastructure.MacDebugBridge.Register("todo-rows", _ =>
        {
            var screenHeight = NSScreen.Screens.FirstOrDefault()?.Frame.Height ?? 0;
            var lines = new List<string> { $"sort={ViewModel.SelectedSort} canReorder={ViewModel.CanReorderTasks} named={ViewModel.IsNamedListSelected} " +
                "menu=" + string.Join(",", _sortItems.Take(6).Select(pair => $"{pair.Kind}{(pair.Item.Hidden ? "[hidden]" : "")}{(pair.Item.State == NSCellStateValue.On ? "[on]" : "")}")) };
            for (var row = 0; row < _entries.Count; row++)
            {
                var rect = _table.ConvertRectToView(_table.RectForRow(row), null);
                var screen = _table.Window?.ConvertRectToScreen(rect) ?? rect;
                var entry = _entries[row];
                var label = entry.Item is { } item ? $"{item.Title} order={item.Task.ManualOrder} selected={_table.IsRowSelected(row)}" : $"[{entry.Group?.Key}]";
                lines.Add($"{row}: {label} at {screen.X:0},{screenHeight - screen.Y - screen.Height:0} {screen.Width:0}x{screen.Height:0}");
            }
            return Task.FromResult(string.Join("\n", lines));
        });
        Infrastructure.MacDebugBridge.Register("todo-sort", args => { ViewModel.SetSortCommand.Execute(args.Length > 0 ? args[0] : "due"); return Task.FromResult(ViewModel.SelectedSort.ToString()); });
        Infrastructure.MacDebugBridge.Register("todo-add", async args =>
        {
            ViewModel.ComposerText = string.Join(' ', args);
            await ViewModel.AddTaskCommand.ExecuteAsync(null);
            return "ok";
        });
        Infrastructure.MacDebugBridge.Register("todo-lists", _ => Task.FromResult(string.Join(" | ", ViewModel.TaskLists.Select((list, index) => $"{index}:{list.Title} sort={list.SortKind}"))));
        // todo-toggle completes or reopens the selected task as its circle does; todo-list-copy, todo-list-export ics|csv and
        // todo-list-import run the list overflow menu's commands.
        Infrastructure.MacDebugBridge.Register("todo-toggle", async _ =>
        {
            if (ViewModel.SelectedTask is not { } task) return "no selected task";
            await ViewModel.ToggleTaskCommand.ExecuteAsync(task);
            return "toggled " + task.Title;
        });
        Infrastructure.MacDebugBridge.Register("todo-delete", args =>
        {
            if (ViewModel.SelectedTask is not { IsReadOnly: false } task) return Task.FromResult("no selected task");
            _ = ViewModel.DeleteTaskCommand.ExecuteAsync(task);
            return Task.FromResult("delete requested for " + task.Title);
        });
        Infrastructure.MacDebugBridge.Register("todo-undo", async _ =>
        {
            if (!ViewModel.UndoTaskActionCommand.CanExecute(null)) return "no undo pending";
            await ViewModel.UndoTaskActionCommand.ExecuteAsync(null);
            return "undone";
        });
        // These open dialogs and panels, so the command returns before they close; the bridge runs one command at a time.
        Infrastructure.MacDebugBridge.Register("todo-list-copy", args =>
        {
            _ = ViewModel.CopySelectedListCommand.ExecuteAsync(null);
            return Task.FromResult("copy list requested");
        });
        Infrastructure.MacDebugBridge.Register("todo-list-export", args =>
        {
            _ = ViewModel.ExportTasksCommand.ExecuteAsync(args.Length > 0 ? args[0] : "ics");
            return Task.FromResult("export requested");
        });
        Infrastructure.MacDebugBridge.Register("todo-list-import", args =>
        {
            _ = ViewModel.ImportTasksCommand.ExecuteAsync(null);
            return Task.FromResult("import requested");
        });
    }
#endif
}
