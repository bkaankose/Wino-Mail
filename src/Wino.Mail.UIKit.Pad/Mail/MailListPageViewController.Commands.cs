using Foundation;
using ObjCRuntime;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Core;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Pad.Mail;

public sealed partial class MailListPageViewController
{
    private MoveFolderPopoverViewController? _movePopover;

    public event EventHandler? CommandStateChanged;

    #region Shell commands (hardware keyboard, multiple-selection panel)

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

    public void Execute(ShellCommand command, UIView? anchor)
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

    private void RunOperation(MailOperation operation, IReadOnlyList<MailItemViewModel> items, MailItemViewModel? composeTarget, UIView? anchor)
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

    /// <summary>The page's own keys: Esc clears the selection, ←/→ close and open threads, Cmd+Z undoes the last mail action.</summary>
    public override UIKeyCommand[] KeyCommands
    {
        get
        {
            var undo = UIKeyCommand.Create(new NSString("z"), UIKeyModifierFlags.Command, new Selector(nameof(UndoMailAction) + ":"));
            undo.DiscoverabilityTitle = new NSString(Translator.UndoActions_Button);
            return
            [
                UIKeyCommand.Create(UIKeyCommand.Escape, 0, new Selector(nameof(ClearSelectionKey) + ":")),
                UIKeyCommand.Create(UIKeyCommand.LeftArrow, 0, new Selector(nameof(CollapseThreadKey) + ":")),
                UIKeyCommand.Create(UIKeyCommand.RightArrow, 0, new Selector(nameof(ExpandThreadKey) + ":")),
                undo
            ];
        }
    }

    [Export(nameof(ClearSelectionKey) + ":")]
    public void ClearSelectionKey(NSObject? sender)
    {
        if (ViewModel.SelectedItemsCount > 0) DeselectAll();
    }

    [Export(nameof(CollapseThreadKey) + ":")]
    public void CollapseThreadKey(NSObject? sender)
    {
        if (_table.IndexPathForSelectedRow is { } path && RowAt(path) is { IsThreadHead: true, IsExpanded: true } row) ToggleThread(row);
    }

    [Export(nameof(ExpandThreadKey) + ":")]
    public void ExpandThreadKey(NSObject? sender)
    {
        if (_table.IndexPathForSelectedRow is { } path && RowAt(path) is { IsThreadHead: true, IsExpanded: false } row) ToggleThread(row);
    }

    /// <summary>Cmd+Z: only while a queued mail action can be undone and the focus is not in text (text keeps its own undo).</summary>
    [Export(nameof(UndoMailAction) + ":")]
    public void UndoMailAction(NSObject? sender)
    {
        if (_released || !_listBound || ViewModel.CurrentVisibleUndoMailActionPack is null) return;
        if (global::Wino.Mail.UIKit.Pad.Shell.PadKeyCommands.FirstResponder(View?.Window) is UITextField or UITextView) return;
        Observe(ViewModel.UndoLatestQueuedActionCommand.ExecuteAsync(null));
    }

    #endregion

    #region Move popover

    /// <summary><see cref="IMailMovePresenter"/>: the reader command bar's Move, anchored to its button.</summary>
    public bool TryShowMove(IReadOnlyList<MailCopy> mails, UIView anchor)
    {
        if (_released || mails.Count == 0) return false;

        var accountIds = mails.Select(static mail => mail.AssignedAccount?.Id).Distinct().ToArray();
        if (accountIds.Length != 1 || accountIds[0] is not Guid accountId || anchor.Window is null) return false;

        Observe(ShowMovePopoverAsync(mails, accountId, anchor));
        return true;
    }

    private void ShowMovePopover(IReadOnlyList<MailItemViewModel> items, UIView? anchor)
    {
        var mails = items.Select(static item => item.MailCopy).ToArray();
        var target = anchor ?? CurrentAnchor();
        if (target is null || !TryShowMove(mails, target))
        {
            // Mixed accounts or no anchor: the shared path picks the folder (or reports the problem).
            Observe(ViewModel.ExecuteMailOperationAsync(new MailOperationPreperationRequest(MailOperation.Move, mails)));
        }
    }

    private async Task ShowMovePopoverAsync(IReadOnlyList<MailCopy> mails, Guid accountId, UIView anchor)
    {
        IReadOnlyList<IMailItemFolder> folders = await _folderService.GetFolderStructureForDisplayAsync(accountId);
        var sourceFolderIds = mails.Select(static mail => mail.AssignedFolder?.Id).OfType<Guid>().ToHashSet();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (_released || anchor.Window is null) return;
            CloseMovePopover();
            var popover = new MoveFolderPopoverViewController(folders, sourceFolderIds, folder =>
                Observe(ViewModel.ExecuteMailOperationAsync(new MailOperationPreperationRequest(MailOperation.Move, mails, moveTargetFolder: folder))));
            _movePopover = popover;
            _host.PresentPopover(popover, anchor, MoveFolderPopoverViewController.PreferredSize, () =>
            {
                if (ReferenceEquals(_movePopover, popover)) _movePopover = null;
            });
        });
    }

    /// <summary>The selected row's cell, or the table: where Move opens from the keyboard.</summary>
    private UIView? CurrentAnchor()
        => _table.IndexPathForSelectedRow is { } path && _table.CellAt(path) is { } cell ? cell : _table;

    private void CloseMovePopover()
    {
        if (_movePopover is { } popover) PadShellHost.DismissPopover(popover);
        _movePopover = null;
    }

    #endregion

    #region Swipe actions

    /// <summary>Leading swipe runs the "right swipe" preference and trailing the "left swipe" one, as on the Mac trackpad.</summary>
    private UISwipeActionsConfiguration? SwipeActions(NSIndexPath path, bool leading)
    {
        if (!_preferences.IsSwipeActionsEnabled || _selectionMode || RowAt(path) is not { } row) return null;

        var swipe = ViewModel.MailActionResolver?.ResolveSwipeActions(row) ?? MailListActionPreferences.GetDefaultSwipeActions(_preferences);
        var operation = leading ? swipe.Right : swipe.Left;
        if (operation == MailOperation.None) return null;

        var items = Leaves(row);
        operation = operation switch
        {
            MailOperation.SetFlag or MailOperation.ClearFlag => items.All(static item => item.IsFlagged) ? MailOperation.ClearFlag : MailOperation.SetFlag,
            MailOperation.MarkAsRead or MailOperation.MarkAsUnread => items.All(static item => item.IsRead) ? MailOperation.MarkAsUnread : MailOperation.MarkAsRead,
            MailOperation.Archive when ViewModel.IsArchiveSpecialFolder => MailOperation.UnArchive,
            _ => operation
        };
        bool destructive = IsDestructive(operation);
        var cell = _table.CellAt(path);
        var action = UIContextualAction.FromContextualActionStyle(destructive ? UIContextualActionStyle.Destructive : UIContextualActionStyle.Normal,
            MailOperationDisplay.GetTitle(operation), (_, _, completion) =>
            {
                RunOperation(operation, items, items.FirstOrDefault(), cell);
                completion(true);
            });
        action.BackgroundColor = operation switch
        {
            MailOperation.SoftDelete or MailOperation.HardDelete => UIColor.SystemRed,
            MailOperation.Archive or MailOperation.UnArchive => UIColor.SystemGreen,
            MailOperation.SetFlag or MailOperation.ClearFlag => UIColor.SystemOrange,
            _ => WinoStyle.Accent
        };
        if (MailOperationDisplay.GetGlyph(operation) is var glyph && glyph != WinoIconGlyph.None)
            action.Image = WinoIcons.MonoImage(glyph, 20, UIColor.White);
        var configuration = UISwipeActionsConfiguration.FromActions([action]);
        configuration.PerformsFirstActionWithFullSwipe = true;
        return configuration;
    }

    #endregion

    #region Context menu

    /// <summary>
    /// The row's long-press (or secondary click) menu: the shared mail actions, Pin, the Category submenu (loaded while the
    /// menu is open) and the thread toggle. A row inside the selection acts on the whole selection.
    /// </summary>
    private UIContextMenuConfiguration? ContextMenu(NSIndexPath path)
    {
        if (RowAt(path) is not { } row || row.SourceItem is not MailItemViewModel item) return null;

        IReadOnlyList<MailItemViewModel> targets = row.IsThreadHead
            ? Leaves(row)
            : ViewModel.IsMailSelected(item.UniqueId) ? ViewModel.SelectedItems.ToArray() : [item];
        if (targets.Count == 0) return null;

        return UIContextMenuConfiguration.Create(null, null, _ =>
        {
            var anchor = _table.CellAt(path);
            var groups = new List<UIMenuElement>();
            var current = new List<UIMenuElement>();
            void Flush()
            {
                if (current.Count == 0) return;
                groups.Add(UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, current.ToArray()));
                current.Clear();
            }

            foreach (var action in ViewModel.GetAvailableMailActions(targets))
            {
                if (action.Operation == MailOperation.Seperator) { Flush(); continue; }
                var title = MailOperationDisplay.GetTitle(action.Operation);
                if (string.IsNullOrEmpty(title)) continue;
                var operation = action.Operation;
                var element = UIAction.Create(operation == MailOperation.Move ? $"{title}…" : title, OperationImage(operation), null,
                    _ => RunOperation(operation, targets, item, anchor));
                if (!action.IsEnabled) element.Attributes |= UIMenuElementAttributes.Disabled;
                if (IsDestructive(operation)) element.Attributes |= UIMenuElementAttributes.Destructive;
                current.Add(element);
            }
            Flush();

            bool allPinned = targets.All(static target => target.MailCopy.IsPinned);
            current.Add(UIAction.Create(allPinned ? Translator.FolderOperation_Unpin : Translator.FolderOperation_Pin,
                WinoIcons.Image(allPinned ? WinoIconGlyph.UnPin : WinoIconGlyph.Pin, 16), null,
                _ => Observe(ViewModel.ChangePinnedStatusAsync(targets, !allPinned))));
            current.Add(CategoryMenu(targets));
            Flush();

            if (row.IsThreadHead && row.Thread is { Count: > 1 })
            {
                current.Add(UIAction.Create(row.IsExpanded ? Translator.MacOS_MailList_CollapseThread : Translator.MacOS_MailList_ExpandThread, null, null, _ => ToggleThread(row)));
                Flush();
            }
            return UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, groups.ToArray());
        });
    }

    private static bool IsDestructive(MailOperation operation) => operation is MailOperation.SoftDelete or MailOperation.HardDelete;

    private static UIImage? OperationImage(MailOperation operation)
        => MailOperationDisplay.GetGlyph(operation) is var glyph && glyph != WinoIconGlyph.None
            ? WinoIcons.Image(glyph, 16, null, MailOperationDisplay.GetTitle(operation))
            : null;

    /// <summary>
    /// The Category submenu (Windows MailContextFlyoutBuilder.CreateCategoriesItem). The categories load while the menu is
    /// open; the submenu is empty when there are none (including a mixed-account selection) or the load fails.
    /// </summary>
    private UIMenu CategoryMenu(IReadOnlyList<MailItemViewModel> targets)
    {
        var deferred = UIDeferredMenuElement.CreateUncached(completion => Observe(LoadCategoriesAsync(targets, elements => completion(elements))));
        return UIMenu.Create(Translator.MailCategoryMenuItem, WinoIcons.Image(WinoIconGlyph.SpecialFolderCategory, 16), UIMenuIdentifier.None, 0, [deferred]);
    }

    private async Task LoadCategoriesAsync(IReadOnlyList<MailItemViewModel> targets, Action<UIMenuElement[]> completion)
    {
        IReadOnlyList<MailCategory> categories = [];
        IReadOnlyCollection<Guid> assigned = [];
        try { (categories, assigned) = await ViewModel.GetAvailableCategoriesAsync(targets); }
        catch (Exception exception) { ReportError(exception); }

        await Dispatcher.ExecuteOnUIThread(() =>
        {
            var favorites = categories.Where(static category => category.IsFavorite).Select(category => CategoryAction(category, assigned, targets)).ToArray();
            var others = categories.Where(static category => !category.IsFavorite).Select(category => CategoryAction(category, assigned, targets)).ToArray();
            completion(
            [
                UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, favorites),
                UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, others)
            ]);
        });
    }

    private UIMenuElement CategoryAction(MailCategory category, IReadOnlyCollection<Guid> assigned, IReadOnlyList<MailItemViewModel> targets)
    {
        // Captured at build time on purpose: the menu closes after a tap and is rebuilt on the next open.
        bool assignedToAll = assigned.Contains(category.Id);
        var color = WinoStyle.FromHexString(category.BackgroundColorHex) ?? WinoStyle.FromHexString(category.TextColorHex);
        var action = UIAction.Create(category.Name ?? string.Empty, WinoIcons.Image(WinoIconGlyph.Dot, 14, color), null,
            _ => Observe(ViewModel.ToggleCategoryAssignmentAsync(category, targets, assignedToAll)));
        action.State = assignedToAll ? UIMenuElementState.On : UIMenuElementState.Off;
        return action;
    }

    #endregion
}
