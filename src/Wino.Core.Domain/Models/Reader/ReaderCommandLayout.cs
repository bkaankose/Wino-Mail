using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Menus;

namespace Wino.Core.Domain.Models.Reader;

public readonly record struct ReaderCommandLayoutEntry(ReaderCommandAction Action, bool IsVisible);

/// <summary>
/// The message state that decides which reader commands exist and which state a toggle pair shows.
/// </summary>
public sealed class ReaderCommandContext
{
    /// <summary>A mail item with a known folder is open. Without it only the built-in commands, Save As and Print exist.</summary>
    public bool HasMailItem { get; init; }
    public bool IsDraft { get; init; }
    public bool IsRead { get; init; }
    public bool IsFlagged { get; init; }
    public SpecialFolderType FolderType { get; init; }
    public bool HasMimeMessage { get; init; }
    public bool CanExportPdf { get; init; }
    public bool CanPrint { get; init; }
}

/// <summary>
/// Order and visibility of the reader command bar, stored as comma-separated action names.
/// Hidden actions carry a leading '-' and are shown in the More menu instead.
/// </summary>
public static class ReaderCommandLayout
{
    private const char HiddenPrefix = '-';
    private const char EntrySeparator = ',';

    public static ReaderCommandLayoutEntry[] CreateDefault() =>
    [
        new(ReaderCommandAction.PopOut, true),
        new(ReaderCommandAction.EditorTheme, true),
        new(ReaderCommandAction.Reply, true),
        new(ReaderCommandAction.ReplyAll, true),
        new(ReaderCommandAction.Forward, true),
        new(ReaderCommandAction.Archive, true),
        new(ReaderCommandAction.Delete, true),
        new(ReaderCommandAction.Flag, true),
        new(ReaderCommandAction.ReadState, true),
        new(ReaderCommandAction.ReaderView, false),
        new(ReaderCommandAction.SaveAs, false),
        new(ReaderCommandAction.Print, false),
        new(ReaderCommandAction.ViewMessageSource, false),
        new(ReaderCommandAction.Junk, false)
    ];

    public static ReaderCommandLayoutEntry[] Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return CreateDefault();

        var entries = new List<ReaderCommandLayoutEntry>();
        var seen = new HashSet<ReaderCommandAction>();

        foreach (var token in value.Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var isVisible = token[0] != HiddenPrefix;
            var name = isVisible ? token : token[1..];

            if (Enum.TryParse<ReaderCommandAction>(name, out var action) && Enum.IsDefined(action) && seen.Add(action))
            {
                entries.Add(new ReaderCommandLayoutEntry(action, isVisible));
            }
        }

        // Actions introduced after the layout was saved keep their default visibility, after the saved ones.
        foreach (var entry in CreateDefault())
        {
            if (seen.Add(entry.Action))
            {
                entries.Add(entry);
            }
        }

        return entries.ToArray();
    }

    public static string Serialize(IEnumerable<ReaderCommandLayoutEntry> entries)
        => string.Join(EntrySeparator, entries.Select(entry => entry.IsVisible ? entry.Action.ToString() : HiddenPrefix + entry.Action.ToString()));

    public static List<IMenuOperation> CreateMenuItems(IEnumerable<ReaderCommandLayoutEntry> entries, ReaderCommandContext context)
    {
        var items = new List<IMenuOperation>();

        foreach (var entry in entries)
        {
            var item = CreateMenuItem(entry.Action, !entry.IsVisible, context);

            if (item != null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private static IMenuOperation CreateMenuItem(ReaderCommandAction action, bool isSecondary, ReaderCommandContext context)
    {
        if (action is ReaderCommandAction.PopOut or ReaderCommandAction.EditorTheme or ReaderCommandAction.ReaderView)
            return ReaderBuiltInMenuItem.Create(action, isSecondary);

        var operation = GetOperation(action, context);

        return operation == MailOperation.None ? null : MailOperationMenuItem.Create(operation, true, isSecondary);
    }

    private static MailOperation GetOperation(ReaderCommandAction action, ReaderCommandContext context)
    {
        if (action == ReaderCommandAction.SaveAs)
            return context.CanExportPdf ? MailOperation.SaveAs : MailOperation.SaveAsEml;

        if (action == ReaderCommandAction.Print)
            return context.CanPrint ? MailOperation.Print : MailOperation.None;

        if (!context.HasMailItem)
            return MailOperation.None;

        return action switch
        {
            ReaderCommandAction.Reply when !context.IsDraft => MailOperation.Reply,
            ReaderCommandAction.ReplyAll when !context.IsDraft => MailOperation.ReplyAll,
            ReaderCommandAction.Forward when !context.IsDraft => MailOperation.Forward,
            ReaderCommandAction.ViewMessageSource when context.HasMimeMessage => MailOperation.ViewMessageSource,
            ReaderCommandAction.Archive => context.FolderType == SpecialFolderType.Archive ? MailOperation.UnArchive : MailOperation.Archive,
            ReaderCommandAction.Delete => MailOperation.SoftDelete,
            ReaderCommandAction.Flag => context.IsFlagged ? MailOperation.ClearFlag : MailOperation.SetFlag,
            ReaderCommandAction.ReadState => context.IsRead ? MailOperation.MarkAsUnread : MailOperation.MarkAsRead,
            ReaderCommandAction.Junk when context.FolderType == SpecialFolderType.Junk => MailOperation.MarkAsNotJunk,
            ReaderCommandAction.Junk when !context.IsDraft && context.FolderType != SpecialFolderType.Sent => MailOperation.MoveToJunk,
            _ => MailOperation.None
        };
    }
}
