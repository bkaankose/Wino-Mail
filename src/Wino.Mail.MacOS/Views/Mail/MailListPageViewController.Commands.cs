using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
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

    /// <summary>
    /// True when the key matches an enabled Mail-mode Delete shortcut that has no Command or Control
    /// modifier. Those are not menu key equivalents (AppDelegate.Shortcuts leaves them to the views),
    /// so the list honours them itself. Key names follow the Mac shortcut recorder.
    /// </summary>
    internal bool IsCustomDeleteShortcut(NSEvent theEvent)
    {
        var flags = theEvent.ModifierFlags;
        if ((flags & (NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ControlKeyMask)) != 0) return false;
        var name = ShortcutKeyName(theEvent);
        if (name is null) return false;
        var modifiers = ModifierKeys.None;
        if (flags.HasFlag(NSEventModifierMask.AlternateKeyMask)) modifiers |= ModifierKeys.Alt;
        if (flags.HasFlag(NSEventModifierMask.ShiftKeyMask)) modifiers |= ModifierKeys.Shift;
        foreach (var shortcut in _shortcuts.EnabledShortcutsSnapshot)
        {
            if (shortcut.Mode == WinoApplicationMode.Mail && shortcut.Action == KeyboardShortcutAction.Delete
                && shortcut.ModifierKeys == modifiers && string.Equals(shortcut.Key?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>The key name the Mac shortcut recorder stores (AppKitDialogService.ShortcutRecorder.KeyName).</summary>
    private static string? ShortcutKeyName(NSEvent theEvent)
    {
        switch (theEvent.KeyCode)
        {
            case 36: return "Enter";
            case 48: return "Tab";
            case 49: return "Space";
            case 51: return "Back";
            case 117: return "Delete";
            case 53: return "Escape";
            case 123: return "Left";
            case 124: return "Right";
            case 125: return "Down";
            case 126: return "Up";
        }
        var characters = theEvent.CharactersIgnoringModifiers;
        if (string.IsNullOrEmpty(characters)) return null;
        var character = char.ToUpperInvariant(characters[0]);
        if (char.IsLetter(character)) return character.ToString();
        if (char.IsDigit(character)) return "Number" + character;
        return null;
    }

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
        AddCategoryMenu(menu, targets);

        if (row.IsThreadHead && row.Thread is { Count: > 1 })
        {
            menu.AddItem(NSMenuItem.SeparatorItem);
            var toggle = new NSMenuItem(row.IsExpanded ? Translator.MacOS_MailList_CollapseThread : Translator.MacOS_MailList_ExpandThread, (_, _) => ToggleThread(row));
            menu.AddItem(toggle);
        }
    }

    // ---- Categories (Windows MailContextFlyoutBuilder.CreateCategoriesItem) ----

    private int _categoryMenuVersion;

    /// <summary>
    /// Adds the Category submenu. The menu is built synchronously, so the categories load in the
    /// background and fill the submenu while the menu is open; the item hides when there are none
    /// (including a mixed-account selection) or the load fails.
    /// </summary>
    private void AddCategoryMenu(NSMenu menu, IReadOnlyList<MailItemViewModel> targets)
    {
        var separator = NSMenuItem.SeparatorItem;
        var submenu = new NSMenu { AutoEnablesItems = false };
        submenu.AddItem(new NSMenuItem(Translator.MacOS_MailList_CategoriesLoading) { Enabled = false });
        var item = new NSMenuItem(Translator.MailCategoryMenuItem)
        {
            Submenu = submenu,
            Image = WinoIcons.Image(WinoIconGlyph.SpecialFolderCategory, 16)
        };
        menu.AddItem(separator);
        menu.AddItem(item);
        int version = ++_categoryMenuVersion;
        _ = LoadCategoryMenuAsync(menu, separator, item, submenu, targets, version);
    }

    private async Task LoadCategoryMenuAsync(NSMenu menu, NSMenuItem separator, NSMenuItem item, NSMenu submenu,
        IReadOnlyList<MailItemViewModel> targets, int version)
    {
        IReadOnlyList<MailCategory> categories = [];
        IReadOnlyCollection<Guid> assigned = [];
        bool failed = false;
        try { (categories, assigned) = await ViewModel.GetAvailableCategoriesAsync(targets).ConfigureAwait(false); }
        catch (Exception exception)
        {
            failed = true;
            ReportError(exception);
        }

        // The main queue also runs while the menu tracks the mouse, so an open menu fills in place.
        CoreFoundation.DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            if (_released || version != _categoryMenuVersion || _contextMenuDelegate is null
                || !ReferenceEquals(menu.Delegate, _contextMenuDelegate)) return;
            if (failed || categories.Count == 0)
            {
                separator.Hidden = true;
                item.Hidden = true;
                return;
            }
            submenu.RemoveAllItems();
            var favorites = categories.Where(static category => category.IsFavorite).ToArray();
            var others = categories.Where(static category => !category.IsFavorite).ToArray();
            foreach (var category in favorites) submenu.AddItem(CategoryItem(category, assigned, targets));
            if (favorites.Length > 0 && others.Length > 0) submenu.AddItem(NSMenuItem.SeparatorItem);
            foreach (var category in others) submenu.AddItem(CategoryItem(category, assigned, targets));
        });
    }

    private NSMenuItem CategoryItem(MailCategory category, IReadOnlyCollection<Guid> assigned, IReadOnlyList<MailItemViewModel> targets)
    {
        bool assignedToAll = assigned.Contains(category.Id);
        var menuItem = new NSMenuItem(category.Name ?? string.Empty,
            (_, _) => Observe(ViewModel.ToggleCategoryAssignmentAsync(category, targets, assignedToAll)))
        {
            State = assignedToAll ? NSCellStateValue.On : NSCellStateValue.Off,
            Image = CategoryImage(category)
        };
        return menuItem;
    }

    /// <summary>A 12pt dot in the category colour; the tinted category glyph when the colour is missing.</summary>
    private static NSImage? CategoryImage(MailCategory category)
    {
        if (WinoStyle.FromHexString(category.BackgroundColorHex) is not { } color)
            return WinoIcons.Image(WinoIconGlyph.SpecialFolderCategory, 14, WinoStyle.FromHexString(category.TextColorHex));
        var image = NSImage.ImageWithSize(new CoreGraphics.CGSize(12, 12), false, rect =>
        {
            color.SetFill();
            NSBezierPath.FromOvalInRect(rect.Inset(1, 1)).Fill();
            return true;
        });
        image.AccessibilityDescription = category.Name;
        return image;
    }

    private sealed class MailContextMenuDelegate(MailListPageViewController owner) : NSMenuDelegate
    {
        public override void MenuWillHighlightItem(NSMenu menu, NSMenuItem item)
        {
        }

        public override void NeedsUpdate(NSMenu menu) => owner.BuildContextMenu(menu);
    }
}
