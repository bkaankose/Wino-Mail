using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Mail;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Lists;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.ToDo;

/// <summary>
/// Manual task order (Windows ToDoPage CanReorderItems, macOS ToDoPageViewController.Reorder): in a named list sorted
/// manually the selected tasks drag within the list, a 2 px accent line marks the insert position (before or after the
/// row under the pointer by its half), and the drop calls <c>ReorderTasksAsync</c>. Move up / Move down in the task
/// menu are the keyboard path (a Linux addition). The drag carries a one-time token; the tasks stay in the process.
/// </summary>
internal sealed partial class ToDoPageView
{
    private const string TaskDragPrefix = "app.winomail.tasks:";
    private const int AutoscrollEdge = 24;
    private const double AutoscrollStep = 8;

    private readonly Dictionary<TaskCell, IDisposable> _taskDragCells = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Gtk.Widget, TaskCell> _taskCellRoots = new(ReferenceEqualityComparer.Instance);
    private readonly Gtk.Box _insertLine = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
    private Gtk.Overlay? _reorderOverlay;
    private TaskItemViewModel[] _draggedTasks = [];
    private string? _dragToken;
    private uint _autoscrollSource;
    private double _autoscrollSpeed;

    internal int TaskDragSourceCount => _taskDragCells.Count;

    internal bool IsInsertLineVisible => _insertLine.GetVisible();

    internal int InsertLineTop => _insertLine.GetMarginTop();

    partial void BuildReorder()
    {
        var self = _self;
        var scroller = _scroller;
        _listHost.SetChild(null);
        var overlay = _reorderOverlay = Gtk.Overlay.New();
        overlay.SetChild(scroller);
        _insertLine.AddCssClass(WinoCssClass.TaskInsertLine);
        _insertLine.SetValign(Gtk.Align.Start);
        _insertLine.SetHalign(Gtk.Align.Fill);
        _insertLine.SetCanTarget(false);
        _insertLine.SetVisible(false);
        overlay.AddOverlay(_insertLine);
        _listHost.SetChild(overlay);

        var list = _list;
        var target = Gtk.DropTarget.New(GObject.Type.String, Gdk.DragAction.Move);
        Bindings.Own(GtkSignals.ConnectReturning<Gtk.DropTarget, Gtk.DropTarget.EnterSignalArgs, Gdk.DragAction>(
            h => target.OnEnter += h, h => target.OnEnter -= h, (_, args) => self.Value?.DragOver(args.X, args.Y) ?? default, default));
        Bindings.Own(GtkSignals.ConnectReturning<Gtk.DropTarget, Gtk.DropTarget.MotionSignalArgs, Gdk.DragAction>(
            h => target.OnMotion += h, h => target.OnMotion -= h, (_, args) => self.Value?.DragOver(args.X, args.Y) ?? default, default));
        Bindings.Own(GtkSignals.Connect<Gtk.DropTarget>(h => target.OnLeave += h, h => target.OnLeave -= h, (_, _) => self.Value?.HideInsertLine()));
        Bindings.Own(GtkSignals.ConnectReturning<Gtk.DropTarget, Gtk.DropTarget.DropSignalArgs, bool>(
            h => target.OnDrop += h, h => target.OnDrop -= h, (_, args) => self.Value?.DropAt(args.Value.GetString(), args.X, args.Y) ?? false, false));
        list.AddController(target);

        var motion = Gtk.DropControllerMotion.New();
        Bindings.Own(GtkSignals.Connect<Gtk.DropControllerMotion, Gtk.DropControllerMotion.MotionSignalArgs>(
            h => motion.OnMotion += h, h => motion.OnMotion -= h, (_, args) => self.Value?.AutoscrollAt(args.Y)));
        Bindings.Own(GtkSignals.Connect<Gtk.DropControllerMotion>(h => motion.OnLeave += h, h => motion.OnLeave -= h, (_, _) => self.Value?.SetAutoscroll(0)));
        scroller.AddController(motion);

        Bindings.Own(new ActionDisposable(() =>
        {
            list.RemoveController(target);
            scroller.RemoveController(motion);
            if (self.Value is not { } view) return;
            view.SetAutoscroll(0);
            foreach (var subscription in view._taskDragCells.Values) subscription.Dispose();
            view._taskDragCells.Clear();
            view._taskCellRoots.Clear();
            view.EndTaskDrag();
        }));
    }

    partial void TaskDragCellSetup(TaskCell cell)
    {
        var self = _self;
        var root = cell.Root;
        _taskCellRoots[root] = cell;
        var source = Gtk.DragSource.New();
        source.SetActions(Gdk.DragAction.Move);
        var prepare = GtkSignals.ConnectReturning<Gtk.DragSource, Gtk.DragSource.PrepareSignalArgs, Gdk.ContentProvider?>(
            h => source.OnPrepare += h, h => source.OnPrepare -= h, (sender, _) =>
            {
                if (self.Value is not { IsReleased: false } view || sender.GetCurrentEventDevice()?.GetSource() == Gdk.InputSource.Touchscreen) return null;
                return cell.Item is { } task && view.BeginTaskDrag(task) is { } token ? Gdk.ContentProvider.NewForValue(new GObject.Value(token)) : null;
            }, null);
        var begin = GtkSignals.Connect<Gtk.DragSource, Gtk.DragSource.DragBeginSignalArgs>(
            h => source.OnDragBegin += h, h => source.OnDragBegin -= h, (_, args) =>
            {
                if (self.Value is not { } view || view._draggedTasks.Length == 0) return;
                Gtk.DragIcon.GetForDrag(args.Drag).SetChild(view.CreateTaskDragCard(cell).Root);
            });
        var end = GtkSignals.Connect<Gtk.DragSource, Gtk.DragSource.DragEndSignalArgs>(
            h => source.OnDragEnd += h, h => source.OnDragEnd -= h, (_, _) => self.Value?.EndTaskDrag());
        var cancel = GtkSignals.ConnectReturning<Gtk.DragSource, Gtk.DragSource.DragCancelSignalArgs, bool>(
            h => source.OnDragCancel += h, h => source.OnDragCancel -= h, (_, _) =>
            {
                self.Value?.EndTaskDrag();
                return false;
            }, false);
        root.AddController(source);
        _taskDragCells[cell] = new ActionDisposable(() =>
        {
            prepare.Dispose();
            begin.Dispose();
            end.Dispose();
            cancel.Dispose();
            root.RemoveController(source);
        });
    }

    partial void TaskDragCellTeardown(TaskCell cell)
    {
        _taskCellRoots.Remove(cell.Root);
        if (_taskDragCells.Remove(cell, out var subscription)) subscription.Dispose();
    }

    /// <summary>The dragged item and, when it is selected, the rest of the selection, in screen order.</summary>
    private string? BeginTaskDrag(TaskItemViewModel task)
    {
        if (!ViewModel.CanReorderTasks || !task.IsEditable) return null;
        var selected = ListViews.SelectedValues<TaskItemViewModel>(_selection);
        _draggedTasks = selected.Contains(task) ? [.. VisibleTasks.Where(selected.Contains)] : [task];
        _dragToken = TaskDragPrefix + Guid.NewGuid().ToString("N");
        return _dragToken;
    }

    private void EndTaskDrag()
    {
        _draggedTasks = [];
        _dragToken = null;
        HideInsertLine();
        SetAutoscroll(0);
    }

    private MailDragCard CreateTaskDragCard(TaskCell cell)
    {
        Gdk.Paintable? image = null;
        try { image = Gtk.WidgetPaintable.New(cell.Card.Root).GetCurrentImage(); }
        catch (Exception exception) { ReportError(exception); }
        return new MailDragCard(_draggedTasks.Length, image, cell.Item?.Title ?? string.Empty);
    }

    /// <summary>The task row under a list point and whether the drop goes after it (lower half).</summary>
    private (TaskCell Cell, bool After)? HitTest(double x, double y)
    {
        for (var widget = _list.Pick(x, y, Gtk.PickFlags.Default); widget is not null && !ReferenceEquals(widget, _list); widget = widget.GetParent())
        {
            if (!_taskCellRoots.TryGetValue(widget, out var cell) || cell.Item is null) continue;
            if (!widget.ComputeBounds(_list, out var bounds)) return null;
            return (cell, y > bounds.GetY() + bounds.GetHeight() / 2);
        }
        return null;
    }

    private Gdk.DragAction DragOver(double x, double y)
    {
        if (_draggedTasks.Length == 0 || !ViewModel.CanReorderTasks || HitTest(x, y) is not { } hit || hit.Cell.Item is not { } target
            || _draggedTasks.Contains(target) || !SameGroup(_draggedTasks[0], target))
        {
            HideInsertLine();
            return default;
        }
        ShowInsertLine(hit.Cell, hit.After);
        return Gdk.DragAction.Move;
    }

    private bool DropAt(string? token, double x, double y)
    {
        HideInsertLine();
        SetAutoscroll(0);
        if (token is null || !string.Equals(token, _dragToken, StringComparison.Ordinal) || HitTest(x, y) is not { } hit || hit.Cell.Item is not { } target) return false;
        var moved = _draggedTasks;
        EndTaskDrag();
        return ReorderTasks(moved, target, hit.After);
    }

    /// <summary>The drop's effect, shared with Move up / Move down and the Debug verbs.</summary>
    internal bool ReorderTasks(TaskItemViewModel[] moved, TaskItemViewModel target, bool insertAfter)
    {
        if (IsReleased || !ViewModel.CanReorderTasks || moved.Length == 0 || moved.Contains(target) || !SameGroup(moved[0], target)) return false;
        Serilog.Log.Information("Task reorder: moving {Count} tasks {Side} a task.", moved.Length, insertAfter ? "after" : "before");
        Observe(ViewModel.ReorderTasksAsync(moved, target, insertAfter));
        return true;
    }

    private bool SameGroup(TaskItemViewModel first, TaskItemViewModel second) => SectionOf(first) == SectionOf(second);

    /// <summary>The neighbour a Move up (-1) or Move down (+1) swaps with, inside the task's group.</summary>
    internal TaskItemViewModel? ReorderNeighbour(TaskItemViewModel task, int direction)
    {
        if (!ViewModel.CanReorderTasks || !task.IsEditable || PositionOf(task) is not { } position) return null;
        var index = (long)position + direction;
        if (index < 0 || index >= _model.Count || _sections.ItemAt((uint)index) is not { } neighbour) return null;
        return SameGroup(task, neighbour) ? neighbour : null;
    }

    internal bool MoveTask(TaskItemViewModel task, int direction)
        => ReorderNeighbour(task, direction) is { } neighbour && ReorderTasks([task], neighbour, direction > 0);

    partial void AppendReorderEntries(WinoMenuBuilder menu, TaskItemViewModel task)
    {
        if (!ViewModel.CanReorderTasks) return;
        var self = _self;
        menu.Separator();
        menu.Item(Translator.MailFilters_MoveUp, () => self.Value?.MoveTask(task, -1), ReorderNeighbour(task, -1) is not null, icon: WinoIconGlyph.ChevronUp);
        menu.Item(Translator.MailFilters_MoveDown, () => self.Value?.MoveTask(task, 1), ReorderNeighbour(task, 1) is not null, icon: WinoIconGlyph.ChevronDown);
    }

    private void ShowInsertLine(TaskCell cell, bool after)
    {
        if (_reorderOverlay is not { } overlay || !cell.Card.Root.ComputeBounds(overlay, out var bounds)) return;
        var y = after ? bounds.GetY() + bounds.GetHeight() + 1 : bounds.GetY() - 3;
        _insertLine.SetMarginTop(Math.Max(0, (int)Math.Round(y)));
        _insertLine.SetMarginStart((int)Math.Round(bounds.GetX()));
        _insertLine.SetMarginEnd(Math.Max(0, overlay.GetWidth() - (int)Math.Round(bounds.GetX() + bounds.GetWidth())));
        _insertLine.SetVisible(true);
    }

    private void HideInsertLine() => _insertLine.SetVisible(false);

    private void AutoscrollAt(double y)
    {
        if (_draggedTasks.Length == 0)
        {
            SetAutoscroll(0);
            return;
        }
        var height = _scroller.GetHeight();
        if (y < AutoscrollEdge) SetAutoscroll(-AutoscrollStep);
        else if (y > height - AutoscrollEdge) SetAutoscroll(AutoscrollStep);
        else SetAutoscroll(0);
    }

    /// <summary>Pixels per 16 ms tick; GTK does not scroll a list under a drag by itself.</summary>
    private void SetAutoscroll(double speed)
    {
        _autoscrollSpeed = speed;
        if (speed == 0 || _autoscrollSource != 0 || IsReleased) return;
        var self = _self;
        _autoscrollSource = GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 16, () => self.Value?.AutoscrollTick() ?? false);
    }

    private bool AutoscrollTick()
    {
        if (IsReleased || _autoscrollSpeed == 0 || _draggedTasks.Length == 0)
        {
            _autoscrollSource = 0;
            _autoscrollSpeed = 0;
            return false;
        }
        var adjustment = _scroller.GetVadjustment();
        adjustment.SetValue(Math.Clamp(adjustment.GetValue() + _autoscrollSpeed, adjustment.GetLower(), Math.Max(adjustment.GetLower(), adjustment.GetUpper() - adjustment.GetPageSize())));
        return true;
    }

#if DEBUG
    internal bool DebugPreviewInsert(TaskItemViewModel task, bool after)
    {
        if (_cells.CellOf(task) is not { } cell) return false;
        ShowInsertLine(cell, after);
        return true;
    }

    internal void DebugHideInsert() => HideInsertLine();

    internal string? DebugBeginDrag(TaskItemViewModel task) => BeginTaskDrag(task);

    internal Gdk.DragAction DebugDragOver(TaskItemViewModel target, bool after)
    {
        if (_cells.CellOf(target) is not { } cell || !cell.Root.ComputeBounds(_list, out var bounds)) return default;
        return DragOver(bounds.GetX() + 10, after ? bounds.GetY() + bounds.GetHeight() - 3 : bounds.GetY() + 3);
    }

    internal bool DebugDrop(string? token, TaskItemViewModel target, bool after)
    {
        if (_cells.CellOf(target) is not { } cell || !cell.Root.ComputeBounds(_list, out var bounds)) return false;
        return DropAt(token, bounds.GetX() + 10, after ? bounds.GetY() + bounds.GetHeight() - 3 : bounds.GetY() + 3);
    }
#endif
}
