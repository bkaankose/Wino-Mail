using Wino.Mail.Linux.Views.Mail;
using Wino.Mail.Linux.Views.Shell;
using Wino.Mail.ViewModels.MenuItems;
using Wino.Mail.ViewModels.MenuItems.Contracts;

namespace Wino.Mail.Linux.Views.ToDo;

/// <summary>
/// The task list drag in progress (macOS ShellSidebarViewController.TaskListDrag): the content provider carries a
/// one-time token, the dragged sidebar item stays in the process. Main thread only.
/// </summary>
internal static class TaskListDragPayload
{
    public const string Prefix = "app.winomail.tasklist:";

    private static string? _token;
    private static MailDragCard? _card;
    private static string? _caption;
    private static bool _refused;

    public static AccountTaskListMenuItem? Current { get; private set; }

    public static string Begin(AccountTaskListMenuItem list)
    {
        End();
        Current = list;
        _token = Prefix + Guid.NewGuid().ToString("N");
        return _token;
    }

    public static AccountTaskListMenuItem? Resolve(string? text)
        => text is not null && _token is not null && string.Equals(text, _token, StringComparison.Ordinal) ? Current : null;

    /// <summary>Idempotent: a drop target ends the drag too, because the source may never report its end.</summary>
    public static void End()
    {
        Current = null;
        _token = null;
        _card = null;
        _caption = null;
        _refused = false;
    }

    public static void SetCard(MailDragCard? card)
    {
        _card = card;
        _caption = null;
        _refused = false;
    }

    public static void SetCaption(string? caption, bool refused)
    {
        if (string.Equals(caption, _caption, StringComparison.Ordinal) && refused == _refused) return;
        _caption = caption;
        _refused = refused;
        _card?.SetCaption(caption, refused);
    }

    /// <summary>Windows CanDropOnTarget: a movable list onto another group of the same account.</summary>
    public static bool CanDrop(AccountTaskListMenuItem list, IMenuItem target)
        => list is { CanMoveToGroup: true, Parameter: { } source }
           && target is AccountTaskListGroupMenuItem { Parameter: { } group }
           && source.MailAccountId == group.MailAccountId
           && source.GroupId != group.Id;
}

/// <summary>Task list rows dropped on task list group rows; the group's <c>DropRequested</c> moves the list.</summary>
internal sealed class TaskListSidebarDropHandler(Action<Exception> error) : ISidebarDropHandler
{
    public bool IsDragActive => TaskListDragPayload.Current is not null;

    public SidebarDropVerdict Validate(IMenuItem item, bool isSelected)
        => TaskListDragPayload.Current is { } list && TaskListDragPayload.CanDrop(list, item) && item is AccountTaskListGroupMenuItem group
            ? new SidebarDropVerdict(Gdk.DragAction.Move, group.Title, false)
            : SidebarDropVerdict.None;

    public void ShowCaption(SidebarDropVerdict verdict) => TaskListDragPayload.SetCaption(verdict.Caption, verdict.Refused);

    public bool Drop(IMenuItem item, bool isSelected, string? text)
    {
        if (TaskListDragPayload.Resolve(text) is not { } list || !TaskListDragPayload.CanDrop(list, item)
            || item is not AccountTaskListGroupMenuItem { DropRequested: { } drop } group) return false;
        Serilog.Log.Information("Task list drop: moving a list into a group.");
        _ = DropAsync(drop, list, group);
        TaskListDragPayload.End();
        return true;
    }

    private async Task DropAsync(Func<IMenuItem, IMenuItem, bool, Task> drop, AccountTaskListMenuItem list, AccountTaskListGroupMenuItem group)
    {
        try { await drop(list, group, false); }
        catch (Exception exception) { error(exception); }
    }
}
