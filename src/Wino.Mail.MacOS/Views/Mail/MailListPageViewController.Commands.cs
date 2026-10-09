using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Controls.Core;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

public sealed partial class MailListPageViewController
{
    private MoveFolderPopover? _movePopover;

    public event EventHandler? CommandStateChanged;

    // ---- Toolbar and Message menu ----

    public bool CanExecute(ShellCommand command)
    {
        if (_released || ViewModel.SelectedItemsCount == 0) return false;
        return command switch
        {
            ShellCommand.Reply or ShellCommand.ReplyAll or ShellCommand.Forward
                => (ViewModel.HasSingleItemSelected || ViewModel.HasSingleFullySelectedThread) && !ViewModel.SelectedItems[0].IsDraft,
            _ => true
        };
    }

    public void Execute(ShellCommand command, NSView? anchor)
    {
        if (!CanExecute(command)) return;
        var items = ViewModel.SelectedItems;
        switch (command)
        {
            case ShellCommand.Archive:
                RunSelectionOperation(ViewModel.IsArchiveSpecialFolder ? MailOperation.UnArchive : MailOperation.Archive);
                break;
            case ShellCommand.Delete:
                RunSelectionOperation(MailOperation.SoftDelete);
                break;
            case ShellCommand.Flag:
                RunSelectionOperation(items.All(static item => item.IsFlagged) ? MailOperation.ClearFlag : MailOperation.SetFlag);
                break;
            case ShellCommand.ToggleRead:
                RunSelectionOperation(items.All(static item => item.IsRead) ? MailOperation.MarkAsUnread : MailOperation.MarkAsRead);
                break;
            case ShellCommand.Move:
                ShowMovePopover(items.ToArray(), anchor);
                break;
            case ShellCommand.Reply:
                RunSelectionOperation(MailOperation.Reply);
                break;
            case ShellCommand.ReplyAll:
                RunSelectionOperation(MailOperation.ReplyAll);
                break;
            case ShellCommand.Forward:
                RunSelectionOperation(MailOperation.Forward);
                break;
        }
    }

    private void RunSelectionOperation(MailOperation operation)
        => Observe(ViewModel.ExecuteMailOperationCommand.ExecuteAsync(operation));

    private void RunOperation(MailOperation operation, IReadOnlyList<MailItemViewModel> items, MailItemViewModel? composeTarget, NSView? anchor)
    {
        if (items.Count == 0) return;
        switch (operation)
        {
            case MailOperation.Reply or MailOperation.ReplyAll or MailOperation.Forward:
                Observe(ViewModel.CreateDraftFromMailAsync(composeTarget ?? items[0], operation));
                return;
            case MailOperation.RetryDraftUpload:
                Observe(ViewModel.RetryDraftUploadAsync(composeTarget ?? items[0]));
                return;
            case MailOperation.Move:
                ShowMovePopover(items, anchor);
                return;
            default:
                Observe(ViewModel.ExecuteMailOperationAsync(new MailOperationPreperationRequest(operation, items.Select(static item => item.MailCopy))));
                return;
        }
    }

    private static IReadOnlyList<MailItemViewModel> Leaves(MailListRow row)
        => (row.IsThreadHead ? row.LeafItems : [row.SourceItem]).OfType<MailItemViewModel>().ToArray();

    // ---- Move popover ----

    private void ShowMovePopover(IReadOnlyList<MailItemViewModel> items, NSView? anchor)
    {
        CloseMovePopover();
        var accountIds = items.Select(static item => item.MailCopy.AssignedAccount?.Id).Distinct().ToArray();
        var target = anchor ?? CurrentAnchor();
        if (accountIds.Length != 1 || accountIds[0] is not Guid accountId || target?.Window is null)
        {
            // Mixed accounts or no anchor: the shared path picks the folder (or reports the problem).
            Observe(ViewModel.ExecuteMailOperationAsync(new MailOperationPreperationRequest(MailOperation.Move, items.Select(static item => item.MailCopy))));
            return;
        }
        Observe(ShowMovePopoverAsync(items, accountId, target));
    }

    private async Task ShowMovePopoverAsync(IReadOnlyList<MailItemViewModel> items, Guid accountId, NSView anchor)
    {
        IReadOnlyList<IMailItemFolder> folders = await _folderService.GetFolderStructureForDisplayAsync(accountId);
        var sourceFolderIds = items.Select(static item => item.MailCopy.AssignedFolder?.Id)
            .Where(static id => id.HasValue).Select(static id => id!.Value).ToHashSet();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (_released || anchor.Window is null) return;
            CloseMovePopover();
            _movePopover = new MoveFolderPopover(folders, sourceFolderIds, folder =>
                Observe(ViewModel.ExecuteMailOperationAsync(new MailOperationPreperationRequest(MailOperation.Move,
                    items.Select(static item => item.MailCopy), moveTargetFolder: folder))));
            _movePopover.Show(anchor);
        });
    }

    private NSView? CurrentAnchor()
    {
        var row = _table.SelectedRow;
        return row >= 0 ? _table.GetRowView(row, false) ?? (NSView)_table : _table;
    }

    private void CloseMovePopover()
    {
        _movePopover?.Close();
        _movePopover?.Dispose();
        _movePopover = null;
    }

    // ---- Trackpad swipe ----

    private NSTableViewRowAction[] SwipeActions(nint index, NSTableRowActionEdge edge)
    {
        if (!_preferences.IsSwipeActionsEnabled || RowAt(index) is not { } row) return [];
        var operation = edge == NSTableRowActionEdge.Leading ? _preferences.RightSwipeOperation : _preferences.LeftSwipeOperation;
        if (operation == MailOperation.None) return [];
        var items = Leaves(row);
        operation = operation switch
        {
            MailOperation.SetFlag or MailOperation.ClearFlag => items.All(static item => item.IsFlagged) ? MailOperation.ClearFlag : MailOperation.SetFlag,
            MailOperation.MarkAsRead or MailOperation.MarkAsUnread => items.All(static item => item.IsRead) ? MailOperation.MarkAsUnread : MailOperation.MarkAsRead,
            MailOperation.Archive when ViewModel.IsArchiveSpecialFolder => MailOperation.UnArchive,
            _ => operation
        };
        bool destructive = operation is MailOperation.SoftDelete or MailOperation.HardDelete;
        var action = NSTableViewRowAction.FromStyle(destructive ? NSTableViewRowActionStyle.Destructive : NSTableViewRowActionStyle.Regular,
            MailOperationPresentation.Title(operation), (_, _) =>
            {
                _table.RowActionsVisible = false;
                RunOperation(operation, items, items.FirstOrDefault(), null);
            });
        action.BackgroundColor = operation switch
        {
            MailOperation.SoftDelete or MailOperation.HardDelete => NSColor.SystemRed,
            MailOperation.Archive or MailOperation.UnArchive => NSColor.SystemGreen,
            MailOperation.SetFlag or MailOperation.ClearFlag => NSColor.SystemOrange,
            _ => WinoStyle.Accent
        };
        action.Image = MailOperationPresentation.Image(operation, 16, NSColor.White);
        return [action];
    }

    // ---- Context menu ----

    private void BuildContextMenu(NSMenu menu)
    {
        menu.RemoveAllItems();
        var clicked = _table.ClickedRow;
        if (RowAt(clicked) is not { } row) return;

        IReadOnlyList<MailItemViewModel> targets;
        var item = (MailItemViewModel)row.SourceItem;
        if (row.IsThreadHead) targets = Leaves(row);
        else targets = ViewModel.IsMailSelected(item.UniqueId) ? ViewModel.SelectedItems.ToArray() : [item];
        if (targets.Count == 0) return;

        NSView? anchor = _table.GetRowView(clicked, false);
        bool previousWasSeparator = true;
        foreach (var action in ViewModel.GetAvailableMailActions(targets))
        {
            if (action.Operation == MailOperation.Seperator)
            {
                if (!previousWasSeparator) menu.AddItem(NSMenuItem.SeparatorItem);
                previousWasSeparator = true;
                continue;
            }
            var title = MailOperationPresentation.Title(action.Operation);
            if (string.IsNullOrEmpty(title)) continue;
            var operation = action.Operation;
            var menuItem = new NSMenuItem(operation == MailOperation.Move ? $"{title}…" : title, (_, _) => RunOperation(operation, targets, item, anchor))
            {
                Enabled = action.IsEnabled
            };
            menuItem.Image = MailOperationPresentation.Image(operation);
            menu.AddItem(menuItem);
            previousWasSeparator = false;
        }

        bool allPinned = targets.All(static target => target.MailCopy.IsPinned);
        if (!previousWasSeparator) menu.AddItem(NSMenuItem.SeparatorItem);
        var pin = new NSMenuItem(allPinned ? Translator.FolderOperation_Unpin : Translator.FolderOperation_Pin,
            (_, _) => Observe(ViewModel.ChangePinnedStatusAsync(targets, !allPinned)))
        {
            Image = WinoIcons.Image(allPinned ? WinoIconGlyph.UnPin : WinoIconGlyph.Pin, 16)
        };
        menu.AddItem(pin);

        if (row.IsThreadHead && row.Thread is { Count: > 1 })
        {
            menu.AddItem(NSMenuItem.SeparatorItem);
            var toggle = new NSMenuItem(row.IsExpanded ? Translator.MacOS_MailList_CollapseThread : Translator.MacOS_MailList_ExpandThread, (_, _) => ToggleThread(row));
            menu.AddItem(toggle);
        }
    }

    private sealed class MailContextMenuDelegate(MailListPageViewController owner) : NSMenuDelegate
    {
        public override void MenuWillHighlightItem(NSMenu menu, NSMenuItem item)
        {
        }

        public override void NeedsUpdate(NSMenu menu) => owner.BuildContextMenu(menu);
    }
}
