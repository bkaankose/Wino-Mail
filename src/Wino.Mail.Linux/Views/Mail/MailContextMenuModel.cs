using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Core;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>One entry of the mail context menu.</summary>
internal sealed record MailMenuEntry(string Title, Func<Task>? Run, bool IsEnabled = true, bool IsSeparator = false,
    bool? IsChecked = null, string? Accelerator = null, MailMenuEntry[]? Children = null,
    WinoIconGlyph Glyph = WinoIconGlyph.None, bool IsDestructive = false, string? GlyphTintHex = null)
{
    public static MailMenuEntry Separator { get; } = new(string.Empty, null, IsSeparator: true);

    public bool CanActivate => !IsSeparator && IsEnabled && (Run is not null || (Children?.Any(static child => child.CanActivate) ?? false));
}

/// <param name="MoveFolders">Null: the targets span accounts or the load failed.</param>
internal sealed record MailContextMenuInput(
    MailListRow Row,
    MailListTargets Targets,
    MailOperationMenuItem[] Actions,
    MailCategory[] Categories,
    HashSet<Guid> AssignedCategoryIds,
    IMailItemFolder[]? MoveFolders,
    HashSet<Guid> SourceFolderIds);

internal sealed record MailContextMenuCommands(
    Func<MailOperation, IMailItemFolder?, Task> RunOperation,
    Func<MailCategory, bool, Task> ToggleCategory,
    Func<bool, Task> SetPinned,
    Func<Task> ToggleThread,
    Func<Task> TestNotification,
    Func<MailOperation, string?> Accelerator);

/// <summary>
/// The mail context menu as plain data: the port of Windows <c>MailContextFlyoutBuilder</c> and macOS
/// <c>BuildContextMenu</c> (the shared operations in their order and groups, Move with the folder tree, the Create submenu,
/// Pin, categories, the thread toggle and, in Debug, the test notification).
/// </summary>
internal static class MailContextMenuModel
{
    private static readonly MailOperation[] AcceleratedWhenEnabled = [MailOperation.Reply, MailOperation.ReplyAll, MailOperation.Forward];

    public static MailMenuEntry[] Build(MailContextMenuInput input, MailContextMenuCommands commands)
    {
        var entries = new List<MailMenuEntry>();
        var actions = input.Actions;
        for (var index = 0; index < actions.Length; index++)
        {
            var action = actions[index];
            var operation = action.Operation;
            if (IsSubmenu(action.Placement))
            {
                var placement = action.Placement;
                var run = new List<MailOperationMenuItem>();
                while (index < actions.Length && actions[index].Placement == placement) run.Add(actions[index++]);
                index--;
                if (Submenu(placement, run, commands) is { } submenu) entries.Add(submenu);
                continue;
            }
            if (IsFocusedInbox(operation)) continue;
            if (operation == MailOperation.Seperator)
            {
                entries.Add(MailMenuEntry.Separator);
                continue;
            }
            if (operation == MailOperation.Move)
            {
                entries.Add(Move(input, commands, action));
                continue;
            }
            if (Leaf(action, MailOperationDisplay.GetTitle(operation), commands) is { } leaf) entries.Add(leaf);
        }

        entries.Add(MailMenuEntry.Separator);
        var items = input.Targets.Items;
        var allPinned = items.Length > 0 && items.All(static item => item.MailCopy.IsPinned);
        entries.Add(new MailMenuEntry(allPinned ? Translator.FolderOperation_Unpin : Translator.FolderOperation_Pin, () => commands.SetPinned(!allPinned),
            Glyph: allPinned ? WinoIconGlyph.UnPin : WinoIconGlyph.Pin));

        if (input.Categories.Length > 0)
        {
            entries.Add(MailMenuEntry.Separator);
            var categories = new List<MailMenuEntry>();
            foreach (var favourite in new[] { true, false })
            {
                if (categories.Count > 0 && input.Categories.Any(category => category.IsFavorite == favourite)) categories.Add(MailMenuEntry.Separator);
                foreach (var category in input.Categories)
                {
                    if (category.IsFavorite != favourite) continue;
                    var assigned = input.AssignedCategoryIds.Contains(category.Id);
                    categories.Add(new MailMenuEntry(category.Name ?? string.Empty, () => commands.ToggleCategory(category, assigned), IsChecked: assigned,
                        Glyph: WinoIconGlyph.SpecialFolderCategory, GlyphTintHex: category.TextColorHex));
                }
            }
            entries.Add(new MailMenuEntry(Translator.MailCategoryMenuItem, null, Children: Normalize(categories), Glyph: WinoIconGlyph.SpecialFolderCategory));
        }

        if (input.Row is { IsThreadHead: true, Thread.Count: > 1 })
        {
            entries.Add(MailMenuEntry.Separator);
            entries.Add(new MailMenuEntry(input.Row.IsExpanded ? Translator.MacOS_MailList_CollapseThread : Translator.MacOS_MailList_ExpandThread, commands.ToggleThread));
        }

#if DEBUG
        entries.Add(MailMenuEntry.Separator);
        entries.Add(new MailMenuEntry(Translator.Buttons_TestNotification, commands.TestNotification));
#endif
        return Normalize(entries);
    }

    // QuickActions is the Windows icon row; like macOS, GTK shows those as ordinary items in the shared order.
    private static bool IsSubmenu(MailOperationMenuPlacement placement) => MailOperationDisplay.GetSubmenuTitle(placement).Length > 0;

    private static bool IsFocusedInbox(MailOperation operation)
        => operation is MailOperation.MoveToFocused or MailOperation.MoveToOther or MailOperation.AlwaysMoveToFocused or MailOperation.AlwaysMoveToOther;

    private static MailMenuEntry? Leaf(MailOperationMenuItem action, string title, MailContextMenuCommands commands)
    {
        var operation = action.Operation;
        if (string.IsNullOrEmpty(title)) return null;
        var accelerator = action.IsEnabled || Array.IndexOf(AcceleratedWhenEnabled, operation) < 0 ? commands.Accelerator(operation) : null;
        return new MailMenuEntry(title, () => commands.RunOperation(operation, null), action.IsEnabled, Accelerator: accelerator,
            Glyph: MailOperationDisplay.GetGlyph(operation), IsDestructive: operation is MailOperation.SoftDelete or MailOperation.HardDelete or MailOperation.DiscardLocalDraft);
    }

    /// <summary>Consecutive actions with the same submenu placement (Create: Task, Event) form one submenu, as on Windows and macOS.</summary>
    private static MailMenuEntry? Submenu(MailOperationMenuPlacement placement, List<MailOperationMenuItem> run, MailContextMenuCommands commands)
    {
        var children = new List<MailMenuEntry>();
        foreach (var child in run)
        {
            if (Leaf(child, MailOperationDisplay.GetSubmenuItemTitle(child.Operation), commands) is { } leaf) children.Add(leaf);
        }
        var title = MailOperationDisplay.GetSubmenuTitle(placement);
        if (children.Count == 0 || string.IsNullOrEmpty(title)) return null;
        MailMenuEntry[] items = [.. children];
        return new MailMenuEntry(title, null, items.Any(static child => child.CanActivate), Children: items, Glyph: MailOperationDisplay.GetSubmenuGlyph(placement));
    }

    private static MailMenuEntry Move(MailContextMenuInput input, MailContextMenuCommands commands, MailOperationMenuItem move)
    {
        var title = MailOperationDisplay.GetTitle(MailOperation.Move);
        var glyph = MailOperationDisplay.GetGlyph(MailOperation.Move);
        // Mixed accounts: the shared request asks for a folder or reports the problem, as on macOS.
        if (input.MoveFolders is null) return new MailMenuEntry(title + "…", () => commands.RunOperation(MailOperation.Move, null), move.IsEnabled, Glyph: glyph);

        var children = new List<MailMenuEntry>();
        foreach (var action in input.Actions)
        {
            if (IsFocusedInbox(action.Operation) && Leaf(action, MailOperationDisplay.GetTitle(action.Operation), commands) is { } leaf) children.Add(leaf);
        }
        var folders = new List<MailMenuEntry>();
        if (move.IsEnabled) AddFolders(folders, input.MoveFolders, input.SourceFolderIds, commands);
        if (children.Count > 0 && folders.Count > 0) children.Add(MailMenuEntry.Separator);
        children.AddRange(folders);
        var normalized = Normalize(children);
        return new MailMenuEntry(title, null, normalized.Any(static child => child.CanActivate), Children: normalized, Glyph: glyph);
    }

    private static void AddFolders(List<MailMenuEntry> destination, IEnumerable<IMailItemFolder?> folders, HashSet<Guid> sourceFolderIds, MailContextMenuCommands commands)
    {
        foreach (var folder in folders)
        {
            if (folder is null) continue;
            var name = folder.ResolvedDisplayName ?? string.Empty;
            var children = new List<MailMenuEntry>();
            if (folder.ChildFolders is { Count: > 0 } childFolders) AddFolders(children, childFolders, sourceFolderIds, commands);
            if (string.IsNullOrWhiteSpace(name))
            {
                destination.AddRange(children);
                continue;
            }
            var valid = folder.IsMoveTarget && !sourceFolderIds.Contains(folder.Id);
            var target = folder;
            var glyph = SpecialFolderGlyphs.GetGlyphOrFolder(folder);
            if (children.Count == 0)
            {
                if (valid) destination.Add(new MailMenuEntry(name, () => commands.RunOperation(MailOperation.Move, target), Glyph: glyph));
                continue;
            }
            var group = new List<MailMenuEntry>();
            if (valid)
            {
                group.Add(new MailMenuEntry(string.Format(Translator.DragMoveToFolderCaption, name), () => commands.RunOperation(MailOperation.Move, target), Glyph: glyph));
                group.Add(MailMenuEntry.Separator);
            }
            group.AddRange(children);
            var normalized = Normalize(group);
            if (normalized.Length > 0) destination.Add(new MailMenuEntry(name, null, Children: normalized, Glyph: glyph));
        }
    }

    /// <summary>No leading, trailing or doubled separators.</summary>
    private static MailMenuEntry[] Normalize(List<MailMenuEntry> entries)
    {
        var result = new List<MailMenuEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.IsSeparator && (result.Count == 0 || result[^1].IsSeparator)) continue;
            result.Add(entry);
        }
        if (result.Count > 0 && result[^1].IsSeparator) result.RemoveAt(result.Count - 1);
        return [.. result];
    }
}
