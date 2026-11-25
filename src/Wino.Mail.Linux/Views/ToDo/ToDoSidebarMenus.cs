using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Shell;
using Wino.Mail.ViewModels.MenuItems;
using Wino.Mail.ViewModels.MenuItems.Contracts;

namespace Wino.Mail.Linux.Views.ToDo;

/// <summary>
/// Sidebar menu entries of the To Do rows (Windows TaskGroupContextRequested / TaskListContextRequested, macOS
/// TaskGroupItems / TaskListItems): new list, rename, ungroup and delete on a list group; rename, remove from group,
/// move to group and delete on a list.
/// </summary>
internal static class ToDoSidebarMenus
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        ShellSidebarContextMenus.AddExtension(Build);
    }

    internal static SidebarMenuEntry[] Build(IMenuItem item, ShellSidebarMenuContext context) => item switch
    {
        AccountTaskListGroupMenuItem group => GroupEntries(group),
        AccountTaskListMenuItem list => ListEntries(list),
        _ => [],
    };

    private static SidebarMenuEntry[] GroupEntries(AccountTaskListGroupMenuItem group)
    {
        var entries = new List<SidebarMenuEntry>();
        if (group.NewListRequested is { } newList)
            entries.Add(new(Translator.ToDoPage_NewList + "…", () => newList(group), Glyph: WinoIconGlyph.New));
        if (group.IsEditable && group.RenameRequested is { } rename)
            entries.Add(new(Translator.ToDoPage_RenameGroup + "…", () => rename(group), Glyph: WinoIconGlyph.Rename));
        if (group.CanUngroup && group.UngroupRequested is { } ungroup)
            entries.Add(new(Translator.ToDoPage_UngroupLists, () => ungroup(group), Glyph: WinoIconGlyph.Move));
        if (group.CanDelete && group.DeleteRequested is { } delete)
            entries.Add(new(Translator.ToDoPage_DeleteGroup, () => delete(group), Glyph: WinoIconGlyph.Delete, IsDestructive: true));
        return [.. entries];
    }

    private static SidebarMenuEntry[] ListEntries(AccountTaskListMenuItem list)
    {
        var entries = new List<SidebarMenuEntry>();
        if (list.CanRename && list.RenameRequested is { } rename)
            entries.Add(new(Translator.ToDoPage_RenameList + "…", () => rename(list), Glyph: WinoIconGlyph.Rename));
        if (list.IsGrouped && list.RemoveFromGroupRequested is { } remove)
            entries.Add(new(Translator.ToDoPage_RemoveFromGroup, () => remove(list), Glyph: WinoIconGlyph.Move));
        if (list.CanMoveToGroup && list.MoveToGroupRequested is { } move)
        {
            var currentGroup = list.Parameter?.GroupId;
            var destinations = new List<SidebarMenuEntry>();
            foreach (var group in list.AvailableGroups)
            {
                if (group is null || group.Id == currentGroup) continue;
                var id = group.Id;
                destinations.Add(new(group.Title ?? string.Empty, () => move(list, id), Glyph: WinoIconGlyph.Folder));
            }
            if (destinations.Count > 0)
                entries.Add(new(Translator.ToDoPage_MoveToGroup, null, Glyph: WinoIconGlyph.Move, Children: [.. destinations]));
        }
        if (list.CanDelete && list.DeleteRequested is { } delete)
        {
            if (entries.Count > 0) entries.Add(SidebarMenuEntry.Separator);
            entries.Add(new(Translator.ToDoPage_DeleteList, () => delete(list), Glyph: WinoIconGlyph.Delete, IsDestructive: true));
        }
        return [.. entries];
    }
}
