using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Core;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>One entry of the mail context menu; <see cref="Glyph"/> and <see cref="IsDestructive"/> are for a renderer that draws them.</summary>
internal sealed record MailMenuEntry(string Title, Func<Task>? Run, bool IsEnabled = true, bool IsSeparator = false,
    bool? IsChecked = null, string? Accelerator = null, MailMenuEntry[]? Children = null,
    WinoIconGlyph Glyph = WinoIconGlyph.None, bool IsDestructive = false)
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
/// <c>BuildContextMenu</c> (compose operations first, the shared operations, Move with the folder tree, Pin, categories,
/// the thread toggle and, in Debug, the test notification).
/// </summary>
internal static class MailContextMenuModel
{
    private static readonly MailOperation[] ComposeOperations = [MailOperation.Reply, MailOperation.ReplyAll, MailOperation.Forward];

    public static MailMenuEntry[] Build(MailContextMenuInput input, MailContextMenuCommands commands)
    {
        var entries = new List<MailMenuEntry>();
        foreach (var operation in ComposeOperations)
        {
            var action = Array.Find(input.Actions, candidate => candidate.Operation == operation);
            var enabled = action?.IsEnabled == true;
            entries.Add(new MailMenuEntry(MailOperationDisplay.GetTitle(operation), () => commands.RunOperation(operation, null), enabled,
                Accelerator: enabled ? commands.Accelerator(operation) : null, Glyph: MailOperationDisplay.GetGlyph(operation)));
        }
        entries.Add(MailMenuEntry.Separator);

        foreach (var action in input.Actions)
        {
            var operation = action.Operation;
            if (Array.IndexOf(ComposeOperations, operation) >= 0 || IsFocusedInbox(operation)) continue;
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
            if (Leaf(action, commands) is { } leaf) entries.Add(leaf);
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
                    categories.Add(new MailMenuEntry(category.Name ?? string.Empty, () => commands.ToggleCategory(category, assigned), IsChecked: assigned));
                }
            }
            entries.Add(new MailMenuEntry(Translator.MailCategoryMenuItem, null, Children: Normalize(categories), Glyph: WinoIconGlyph.Tag));
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

    private static bool IsFocusedInbox(MailOperation operation)
        => operation is MailOperation.MoveToFocused or MailOperation.MoveToOther or MailOperation.AlwaysMoveToFocused or MailOperation.AlwaysMoveToOther;

    private static MailMenuEntry? Leaf(MailOperationMenuItem action, MailContextMenuCommands commands)
    {
        var operation = action.Operation;
        var title = MailOperationDisplay.GetTitle(operation);
        if (string.IsNullOrEmpty(title)) return null;
        return new MailMenuEntry(title, () => commands.RunOperation(operation, null), action.IsEnabled, Accelerator: commands.Accelerator(operation),
            Glyph: MailOperationDisplay.GetGlyph(operation), IsDestructive: operation is MailOperation.SoftDelete or MailOperation.HardDelete or MailOperation.DiscardLocalDraft);
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
            if (IsFocusedInbox(action.Operation) && Leaf(action, commands) is { } leaf) children.Add(leaf);
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
            if (children.Count == 0)
            {
                if (valid) destination.Add(new MailMenuEntry(name, () => commands.RunOperation(MailOperation.Move, target)));
                continue;
            }
            var group = new List<MailMenuEntry>();
            if (valid)
            {
                group.Add(new MailMenuEntry(string.Format(Translator.DragMoveToFolderCaption, name), () => commands.RunOperation(MailOperation.Move, target)));
                group.Add(MailMenuEntry.Separator);
            }
            group.AddRange(children);
            var normalized = Normalize(group);
            if (normalized.Length > 0) destination.Add(new MailMenuEntry(name, null, Children: normalized));
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
