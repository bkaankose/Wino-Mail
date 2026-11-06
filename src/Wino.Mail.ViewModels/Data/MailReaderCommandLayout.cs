using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Presentation;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One primary reader command: an operation and the reader menu item that runs it. <see cref="Item"/> is null for the
/// disabled placeholder set shown while no message is loaded.
/// </summary>
public sealed record MailReaderCommandSlot(MailOperation Operation, MailOperationMenuItem? Item, bool IsEnabled)
{
    public string Title => MailReaderCommandLayout.BarTitle(Operation);
}

/// <summary>What the reader command bar shows: a listed message, a saved message file, or the disabled default set.</summary>
public enum MailReaderCommandPlanKind
{
    Message,
    File,
    Placeholder
}

/// <summary>
/// The reader command bar split into primary commands (null entries are group separators) and the More menu content.
/// </summary>
public sealed record MailReaderCommandPlan(MailReaderCommandPlanKind Kind, IReadOnlyList<MailReaderCommandSlot?> Primary, IReadOnlyList<MailOperationMenuItem> Overflow);

/// <summary>
/// The Windows reader command bar (OperationCommandBar) as native heads lay it out from
/// <see cref="MailRenderingPageViewModel.MenuItems"/>: Reply, Reply all and Forward, then the message actions in a fixed
/// order, everything else in More. A saved message file (no list item) shows only its own operations; a reader without a
/// loaded message shows the default set disabled. Shared by the Mac, iPhone, iPad and Android readers.
/// </summary>
public static class MailReaderCommandLayout
{
    private static readonly MailOperation[] PrimaryOrder =
    [
        MailOperation.Reply, MailOperation.ReplyAll, MailOperation.Forward, MailOperation.None,
        MailOperation.Archive, MailOperation.UnArchive, MailOperation.SoftDelete, MailOperation.HardDelete, MailOperation.Move,
        MailOperation.SetFlag, MailOperation.ClearFlag, MailOperation.MarkAsRead, MailOperation.MarkAsUnread
    ];

    private static readonly MailOperation[] PlaceholderOrder =
    [
        MailOperation.Reply, MailOperation.ReplyAll, MailOperation.Forward, MailOperation.None,
        MailOperation.Archive, MailOperation.SoftDelete, MailOperation.Move, MailOperation.SetFlag, MailOperation.MarkAsRead
    ];

    /// <summary>The short label the Windows command bar shows ("Flag" rather than "Set flag").</summary>
    public static string BarTitle(MailOperation operation)
        => operation == MailOperation.SetFlag ? Translator.MailOperation_Flag : MailOperationDisplay.GetTitle(operation);

    /// <param name="menuItems">The reader ViewModel's menu items.</param>
    /// <param name="hasListItem">True when the reader shows a message from a list (not a saved message file).</param>
    /// <param name="offerMove">
    /// Adds Move next to Delete for a listed message. The shared reader menu has no Move (Windows moves from the list);
    /// the touch readers offer it through their Move sheet or popover.
    /// </param>
    public static MailReaderCommandPlan Build(IEnumerable<IMenuOperation> menuItems, bool hasListItem, bool offerMove)
    {
        var all = menuItems.OfType<MailOperationMenuItem>().ToArray();
        var items = all.Where(static item => item.Operation != MailOperation.Seperator).ToArray();
        var primary = new List<MailReaderCommandSlot?>();

        if (!items.Any(static item => PrimaryOrder.Contains(item.Operation)))
        {
            if (!hasListItem && items.Length > 0)
            {
                // A saved .eml has only Save As and Print; show just those, like Windows.
                foreach (var item in items) primary.Add(new MailReaderCommandSlot(item.Operation, item, item.IsEnabled));
                return new MailReaderCommandPlan(MailReaderCommandPlanKind.File, primary, Array.Empty<MailOperationMenuItem>());
            }
            // Nothing loaded yet (or the message failed to load): the default set, disabled, like the Windows bar.
            foreach (var operation in PlaceholderOrder)
                primary.Add(operation == MailOperation.None ? null : new MailReaderCommandSlot(operation, null, false));
            return new MailReaderCommandPlan(MailReaderCommandPlanKind.Placeholder, primary, all);
        }

        if (offerMove && hasListItem && items.Any(static item => item.Operation is MailOperation.SoftDelete or MailOperation.HardDelete)
            && !items.Any(static item => item.Operation == MailOperation.Move))
            items = [.. items, MailOperationMenuItem.Create(MailOperation.Move)];

        var shown = new HashSet<MailOperation>();
        foreach (var operation in PrimaryOrder)
        {
            if (operation == MailOperation.None)
            {
                primary.Add(null);
                continue;
            }
            if (items.FirstOrDefault(candidate => candidate.Operation == operation) is not { } item) continue;
            shown.Add(operation);
            primary.Add(new MailReaderCommandSlot(operation, item, item.IsEnabled));
        }
        if (primary.Count > 0 && primary[^1] is null) primary.RemoveAt(primary.Count - 1);
        return new MailReaderCommandPlan(MailReaderCommandPlanKind.Message, primary, all.Where(item => !shown.Contains(item.Operation)).ToArray());
    }
}
