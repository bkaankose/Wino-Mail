using Wino.Mail.Linux.Views.Mail;
using Wino.Mail.Linux.Views.ToDo;
using Wino.Mail.ViewModels.MenuItems;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Shell;

/// <summary>
/// Task list rows as drag sources (Windows ShellItem_DragStarting, macOS ShellSidebarViewController.TaskListDrag): one
/// drag source per cell that only starts on a movable task list; the drop lands through <see cref="TaskListSidebarDropHandler"/>.
/// </summary>
internal sealed partial class ShellSidebarView
{
    private readonly Dictionary<ShellSidebarCell, IDisposable> _taskListDragCells = new(ReferenceEqualityComparer.Instance);

    internal int TaskListDragSourceCount => _taskListDragCells.Count;

    private void SetUpTaskListDrag()
    {
        AddDropHandler(new TaskListSidebarDropHandler(_error));
        var previousSetup = CellSetup;
        var previousTeardown = CellTeardown;
        CellSetup = (cell, listItem) =>
        {
            previousSetup?.Invoke(cell, listItem);
            TaskListDragCellSetup(cell);
        };
        CellTeardown = cell =>
        {
            previousTeardown?.Invoke(cell);
            if (_taskListDragCells.Remove(cell, out var subscription)) subscription.Dispose();
        };
        _bindings.Own(new ActionDisposable(() =>
        {
            foreach (var subscription in _taskListDragCells.Values) subscription.Dispose();
            _taskListDragCells.Clear();
            TaskListDragPayload.End();
        }));
    }

    private void TaskListDragCellSetup(ShellSidebarCell cell)
    {
        if (_disposed) return;
        var root = cell.Root;
        var source = Gtk.DragSource.New();
        source.SetActions(Gdk.DragAction.Move);
        var prepare = GtkSignals.ConnectReturning<Gtk.DragSource, Gtk.DragSource.PrepareSignalArgs, Gdk.ContentProvider?>(
            h => source.OnPrepare += h, h => source.OnPrepare -= h, (sender, _) =>
            {
                if (cell.Item is not AccountTaskListMenuItem { CanMoveToGroup: true } list
                    || sender.GetCurrentEventDevice()?.GetSource() == Gdk.InputSource.Touchscreen) return null;
                return Gdk.ContentProvider.NewForValue(new GObject.Value(TaskListDragPayload.Begin(list)));
            }, null);
        var begin = GtkSignals.Connect<Gtk.DragSource, Gtk.DragSource.DragBeginSignalArgs>(
            h => source.OnDragBegin += h, h => source.OnDragBegin -= h, (_, args) =>
            {
                if (TaskListDragPayload.Current is not { } list) return;
                Gdk.Paintable? image = null;
                try { image = Gtk.WidgetPaintable.New(cell.Row.Root).GetCurrentImage(); }
                catch (Exception exception) { _error(exception); }
                var card = new MailDragCard(1, image, list.Title);
                Gtk.DragIcon.GetForDrag(args.Drag).SetChild(card.Root);
                TaskListDragPayload.SetCard(card);
            });
        var end = GtkSignals.Connect<Gtk.DragSource, Gtk.DragSource.DragEndSignalArgs>(
            h => source.OnDragEnd += h, h => source.OnDragEnd -= h, (_, _) => TaskListDragPayload.End());
        var cancel = GtkSignals.ConnectReturning<Gtk.DragSource, Gtk.DragSource.DragCancelSignalArgs, bool>(
            h => source.OnDragCancel += h, h => source.OnDragCancel -= h, (_, _) =>
            {
                TaskListDragPayload.End();
                return false;
            }, false);
        root.AddController(source);
        _taskListDragCells[cell] = new ActionDisposable(() =>
        {
            prepare.Dispose();
            begin.Dispose();
            end.Dispose();
            cancel.Dispose();
            root.RemoveController(source);
        });
    }
}
