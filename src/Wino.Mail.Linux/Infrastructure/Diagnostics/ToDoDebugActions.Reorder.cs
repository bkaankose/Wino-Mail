#if DEBUG
using System.Globalization;
using Wino.Mail.Linux.Views.ToDo;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.MenuItems;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Lists;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Reorder verbs (TD-05): order, move-up n, move-down n, reorder from to before|after (the drag source, the drop
/// target's motion and drop code with a real token), reorder-preview n before|after, reorder-hide, reorder-state,
/// list-drop listTitle groupTitle (the sidebar drop handler with a real token).
/// </summary>
internal static partial class ToDoDebugActions
{
    static partial void AddReorderVerbs()
    {
        Verb("order", _ => Order(Page));
        Verb("move-up", args => Page.MoveTask(TaskAt(args, 0), -1) ? Order(Page) : "not reorderable");
        Verb("move-down", args => Page.MoveTask(TaskAt(args, 0), 1) ? Order(Page) : "not reorderable");
        Verb("reorder", args =>
        {
            var page = Page;
            var source = TaskAt(args, 0);
            var target = TaskAt(args, 1);
            var after = args.Length > 2 && args[2] == "after";
            ListViews.Select(page.Selection, [page.PositionOf(source) ?? 0]);
            if (page.DebugBeginDrag(source) is not { } token) return "not reorderable";
            var action = page.DebugDragOver(target, after);
            var line = page.IsInsertLineVisible;
            var dropped = page.DebugDrop(token, target, after);
            return $"motion={action} line={line} dropped={dropped}";
        });
        Verb("reorder-preview", args =>
        {
            var page = Page;
            return page.DebugPreviewInsert(TaskAt(args, 0), args.Length > 1 && args[1] == "after")
                ? $"line top={page.InsertLineTop}" : "no cell";
        });
        Verb("reorder-hide", _ =>
        {
            Page.DebugHideInsert();
            return "hidden";
        });
        Verb("reorder-state", _ =>
        {
            var page = Page;
            var sidebar = Shell.Sidebar;
            return $"canReorder={page.ViewModel.CanReorderTasks} sort={page.ViewModel.SelectedSort} dragSources={page.TaskDragSourceCount} cells={page.Cells.CellCount} "
                + $"line={page.IsInsertLineVisible} sidebarDragSources={sidebar.TaskListDragSourceCount}";
        });
        Verb("list-drop", args =>
        {
            if (args.Length < 2) throw new ArgumentException("list-drop needs a list and a group");
            var sidebar = Shell.Sidebar;
            if (SidebarDebugActions.Resolve(sidebar, args[0]) is not AccountTaskListMenuItem list) return "no list " + args[0];
            var group = SidebarDebugActions.Resolve(sidebar, args[1]);
            var token = TaskListDragPayload.Begin(list);
            var verdict = sidebar.DebugValidateDrop(group);
            var dropped = sidebar.DebugDrop(group, token);
            TaskListDragPayload.End();
            return $"verdict={verdict.Action} caption='{verdict.Caption}' dropped={dropped}";
        });
    }

    private static TaskItemViewModel TaskAt(string[] args, int index)
    {
        var position = int.Parse(args.Length > index ? args[index] : "0", CultureInfo.InvariantCulture);
        var tasks = Page.VisibleTasks;
        return position >= 0 && position < tasks.Length ? tasks[position] : throw new ArgumentException("no task " + position);
    }

    private static string Order(ToDoPageView page)
        => string.Join(" | ", page.VisibleTasks.Select(static (task, index) => $"{index}:{task.Title} ({task.Task?.ManualOrder})"));
}
#endif
