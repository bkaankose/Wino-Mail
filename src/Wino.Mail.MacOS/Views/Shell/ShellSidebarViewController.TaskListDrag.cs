using AppKit;
using Wino.Core.Domain.MenuItems;

namespace Wino.Mail.MacOS.Views.Shell;

/// <summary>
/// Task lists drag onto task list group rows of the same account (Windows ShellMenuTemplates
/// ShellItem_DragStarting / ShellItem_Drop). The drag stays inside the pane, so the pasteboard only
/// marks it; the dragged list is kept here and the group's DropRequested performs the move.
/// </summary>
internal sealed partial class ShellSidebarViewController
{
    internal const string TaskListPasteboardType = "com.winomail.tasklist";
    private AccountTaskListMenuItem? _draggedTaskList;

    /// <summary>The pasteboard writer for a draggable task list row; null for every other row.</summary>
    private NSPasteboardItem? TaskListPasteboardWriter(object item)
    {
        if (item is not AccountTaskListMenuItem { CanMoveToGroup: true } list) return null;
        _draggedTaskList = list;
        var pasteboardItem = new NSPasteboardItem();
        pasteboardItem.SetStringForType(list.Parameter.Id.ToString(), TaskListPasteboardType);
        return pasteboardItem;
    }

    private static bool IsTaskListDrag(INSDraggingInfo info)
        => info.DraggingPasteboard.Types?.Contains(TaskListPasteboardType) == true;

    private AccountTaskListGroupMenuItem? TaskListGroupAt(INSDraggingInfo info, out nint row)
    {
        row = _outline.GetRow(_outline.ConvertPointFromView(info.DraggingLocation, null));
        if (_draggedTaskList is not { } list || row < 0 || row >= _outline.RowCount || _outline.ItemAtRow(row) is not Node node) return null;
        return node.Item is AccountTaskListGroupMenuItem group
               && group.Parameter.MailAccountId == list.Parameter.MailAccountId
               && list.Parameter.GroupId != group.Parameter.Id
            ? group
            : null;
    }

    private NSDragOperation ValidateTaskListDrop(INSDraggingInfo info)
    {
        if (TaskListGroupAt(info, out var row) is null)
        {
            SetDropRow(-1);
            return NSDragOperation.None;
        }

        if (_outline.ItemAtRow(row) is { } node) _outline.SetDropItem(node, DropOnItem);
        SetDropRow(row);
        return NSDragOperation.Move;
    }

    private bool AcceptTaskListDrop(INSDraggingInfo info)
    {
        var group = TaskListGroupAt(info, out _);
        var list = _draggedTaskList;
        SetDropRow(-1);
        _draggedTaskList = null;
        if (group?.DropRequested is null || list is null) return false;
        _ = DropTaskListAsync(group, list);
        return true;
    }

    private async Task DropTaskListAsync(AccountTaskListGroupMenuItem group, AccountTaskListMenuItem list)
    {
        try { await group.DropRequested(list, group, false); }
        catch (Exception exception) { _error(exception); }
    }
}
