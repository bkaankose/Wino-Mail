using Wino.Mail.Linux.Views.Common;

namespace Wino.Mail.Linux.Views.ToDo;

/// <summary>
/// The drawer host: <see cref="ToDoDetailDrawer"/> follows the selection (one task, several, none) and shows a Back
/// button instead of Close while the layout is swapped.
/// </summary>
internal sealed partial class ToDoPageView
{
    private ToDoDetailDrawer? _drawer;

    internal ToDoDetailDrawer? Drawer => _drawer;

    partial void BuildDrawer(Adw.Bin drawerHost)
    {
        var drawer = _drawer = Bindings.Own(new ToDoDetailDrawer(ViewModel, Dispatcher, Observe, ReportError));
        drawerHost.SetChild(drawer.Root);
        Bind.BindAny(static vm => (vm.SelectedTask, vm.IsMultipleTaskSelection, vm.SelectedTaskCountText, vm.CanEditSelectedTasks,
            vm.CanEditSelectedTask, vm.CanCreateCalendarEvent, vm.AreStepsAvailableForSelectedTask, vm.HasSelectedTaskLinks), _ => drawer.Refresh());
    }

    partial void LayoutStateChanged(EndPaneState state) => _drawer?.SetCompact(state == EndPaneState.Swapped);
}
