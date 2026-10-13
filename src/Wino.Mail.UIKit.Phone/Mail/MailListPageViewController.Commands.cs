using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.UIKit.MailList;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Mail;

public sealed partial class MailListPageViewController
{
    private UIBarButtonItem? _editItem;
    private UIBarButtonItem? _doneItem;
    private UIBarButtonItem? _composeItem;
    private UIBarButtonItem? _selectAllItem;
    private UINavigationController? _moveSheet;
    private UINavigationController? _filterSheet;
    private UIBarButtonItem[] _editToolbar = [];
    private UIButton[] _editButtons = [];

    // ---- Navigation bar and edit mode ----

    private void BuildBarItems()
    {
        _composeItem = PhoneBarItems.Compose(Compose);
        _editItem = new UIBarButtonItem(Translator.Buttons_Edit, UIBarButtonItemStyle.Plain, (_, _) => ViewModel.IsMultiSelectionModeEnabled = true);
        _doneItem = new UIBarButtonItem(Translator.Buttons_Done, UIBarButtonItemStyle.Done, (_, _) => ViewModel.IsMultiSelectionModeEnabled = false);
        _selectAllItem = new UIBarButtonItem(Translator.MacOSMenu_SelectAll, UIBarButtonItemStyle.Plain, (_, _) => ToggleSelectAll());
        var more = PhoneBarItems.RowButton(WinoIconGlyph.More, Translator.More, null);
        more.Menu = UIMenu.Create([UIDeferredMenuElement.CreateUncached(completion => completion(SelectionMenuElements(ViewModel.SelectedItems.ToArray())))]);
        _editButtons =
        [
            PhoneBarItems.RowButton(WinoIconGlyph.Archive, Translator.MailOperation_Archive, () => RunOnSelection(ViewModel.IsArchiveSpecialFolder ? MailOperation.UnArchive : MailOperation.Archive)),
            PhoneBarItems.RowButton(WinoIconGlyph.Delete, Translator.MailOperation_Delete, () => RunOnSelection(MailOperation.SoftDelete)),
            PhoneBarItems.RowButton(WinoIconGlyph.Move, Translator.MailOperation_Move, () => ShowMoveSheet(ViewModel.SelectedItems.ToArray())),
            PhoneBarItems.RowButton(WinoIconGlyph.Flag, Translator.MailOperation_Flag, () =>
                RunOnSelection(ViewModel.SelectedItems.All(static item => item.IsFlagged) ? MailOperation.ClearFlag : MailOperation.SetFlag)),
            more
        ];
        _editToolbar = PhoneBarItems.Row(_editButtons);
        ApplyNavigationItems();
    }

    private void ApplyNavigationItems()
    {
        if (_composeItem is null || _editItem is null || _doneItem is null || _selectAllItem is null) return;
        if (_selectionMode)
        {
            NavigationItem.SetHidesBackButton(true, true);
            NavigationItem.SetRightBarButtonItems([_doneItem], true);
            NavigationItem.SetLeftBarButtonItems([_selectAllItem], true);
        }
        else
        {
            NavigationItem.SetLeftBarButtonItems([], true);
            NavigationItem.SetHidesBackButton(false, true);
            NavigationItem.SetRightBarButtonItems([_composeItem, _editItem], true);
        }
    }

    /// <summary>Edit mode: round checks on the rows, multiple selection, the selection toolbar and no swipe actions.</summary>
    private void ApplySelectionMode(bool enabled)
    {
        if (_selectionMode == enabled && IsViewLoaded && _table.AllowsMultipleSelection == enabled) return;
        _selectionMode = enabled;
        ClearSelection();
        _table.AllowsMultipleSelection = enabled;
        foreach (var cell in _table.VisibleCells.OfType<WinoMailRowCell>()) cell.IsSelectionMode = enabled;
        // The check narrows the text column, which can wrap the tile line.
        NoteHeightsChanged();
        ToolbarItems = enabled ? _editToolbar : [];
        // The selection toolbar takes the tab bar's place, as the reader toolbar does.
        if (TabBarController is { } tabs)
        {
            if (OperatingSystem.IsIOSVersionAtLeast(18)) tabs.SetTabBarHidden(enabled, true);
            else tabs.TabBar.Hidden = enabled;
        }
        NavigationController?.SetToolbarHidden(!enabled, true);
        ApplyNavigationItems();
        UpdateEditToolbar();
        UpdateTitle();
    }

    private void UpdateEditToolbar()
    {
        bool any = ViewModel.SelectedItemsCount > 0;
        foreach (var button in _editButtons) button.Enabled = any;
        if (_selectAllItem is not null) _selectAllItem.Title = ViewModel.IsAllItemsSelected && any ? Translator.Buttons_UnselectAll : Translator.MacOSMenu_SelectAll;
    }

    private void ToggleSelectAll()
    {
        if (ViewModel.IsAllItemsSelected && ViewModel.SelectedItemsCount > 0) ClearSelection();
        else SelectAllRows();
    }

    private void Compose()
    {
        if (_mailClient is { } mail) Observe(mail.OnMenuItemInvokedAsync(mail.CreatePrimaryMenuItem));
    }

    // ---- Operations ----

    private void RunOnSelection(MailOperation operation)
    {
        if (ViewModel.SelectedItemsCount == 0) return;
        Observe(ViewModel.ExecuteMailOperationCommand.ExecuteAsync(operation));
        if (_selectionMode && operation is MailOperation.Archive or MailOperation.UnArchive or MailOperation.SoftDelete or MailOperation.HardDelete)
            ViewModel.IsMultiSelectionModeEnabled = false;
    }

    /// <summary>Runs an operation on explicit targets (swipe, long press), like the Mac list.</summary>
    private void RunOperation(MailOperation operation, IReadOnlyList<MailItemViewModel> items, MailItemViewModel? composeTarget)
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
                ShowMoveSheet(items);
                return;
            default:
                Observe(ViewModel.ExecuteMailOperationAsync(new MailOperationPreperationRequest(operation, items.Select(static item => item.MailCopy))));
                return;
        }
    }

    private static IReadOnlyList<MailItemViewModel> Leaves(MailListRow row)
        => (row.IsThreadHead ? row.LeafItems : new[] { row.SourceItem }).OfType<MailItemViewModel>().ToArray();

    /// <summary>The mails a long press or swipe acts on: a thread's leaves, the edit-mode selection, or the row.</summary>
    private IReadOnlyList<MailItemViewModel> Targets(MailListRow row)
    {
        if (row.IsThreadHead) return Leaves(row);
        if (row.SourceItem is not MailItemViewModel item) return Array.Empty<MailItemViewModel>();
        return _selectionMode && ViewModel.IsMailSelected(item.UniqueId) ? ViewModel.SelectedItems.ToArray() : new[] { item };
    }

    // ---- Swipe actions (leading Archive; trailing Delete, Flag and More) ----

    private UISwipeActionsConfiguration? SwipeActions(NSIndexPath indexPath, bool leading)
    {
        if (_selectionMode || RowAt(indexPath) is not { } row) return null;
        var items = Leaves(row);
        if (items.Count == 0) return null;
        var composeTarget = row.SourceItem as MailItemViewModel;
        UIContextualAction[] actions;
        if (leading)
        {
            var archive = ViewModel.IsArchiveSpecialFolder ? MailOperation.UnArchive : MailOperation.Archive;
            actions = [Swipe(archive, UIColor.SystemGreen, () => RunOperation(archive, items, composeTarget))];
        }
        else
        {
            var flag = items.All(static item => item.IsFlagged) ? MailOperation.ClearFlag : MailOperation.SetFlag;
            actions =
            [
                Swipe(MailOperation.SoftDelete, UIColor.SystemRed, () => RunOperation(MailOperation.SoftDelete, items, composeTarget)),
                Swipe(flag, UIColor.SystemOrange, () => RunOperation(flag, items, composeTarget)),
                SwipeMore(row, items, composeTarget)
            ];
        }
        var configuration = UISwipeActionsConfiguration.FromActions(actions);
        configuration.PerformsFirstActionWithFullSwipe = true;
        return configuration;
    }

    private static UIContextualAction Swipe(MailOperation operation, UIColor color, Action run)
    {
        var title = operation == MailOperation.SetFlag ? Translator.MailOperation_Flag : MailOperationDisplay.GetTitle(operation);
        var action = UIContextualAction.FromContextualActionStyle(UIContextualActionStyle.Normal, title, (_, _, done) =>
        {
            run();
            done(true);
        });
        action.BackgroundColor = color;
        action.Image = WinoIcons.TemplateImage(MailOperationDisplay.GetGlyph(operation), 20);
        return action;
    }

    /// <summary>The trailing More action: an action sheet with the rest of the shared actions for the row.</summary>
    private UIContextualAction SwipeMore(MailListRow row, IReadOnlyList<MailItemViewModel> items, MailItemViewModel? composeTarget)
    {
        var action = UIContextualAction.FromContextualActionStyle(UIContextualActionStyle.Normal, Translator.More, (_, _, done) =>
        {
            done(true);
            var sheet = UIAlertController.Create(null, null, UIAlertControllerStyle.ActionSheet);
            foreach (var available in ViewModel.GetAvailableMailActions(items))
            {
                if (available.Operation is MailOperation.Seperator or MailOperation.Archive or MailOperation.UnArchive or MailOperation.SoftDelete
                    or MailOperation.SetFlag or MailOperation.ClearFlag || !available.IsEnabled) continue;
                var title = MailOperationDisplay.GetTitle(available.Operation);
                if (string.IsNullOrEmpty(title)) continue;
                var operation = available.Operation;
                sheet.AddAction(UIAlertAction.Create(operation == MailOperation.Move ? $"{title}…" : title, UIAlertActionStyle.Default,
                    _ => RunOperation(operation, items, composeTarget)));
            }
            bool allPinned = items.All(static item => item.MailCopy.IsPinned);
            sheet.AddAction(UIAlertAction.Create(allPinned ? Translator.FolderOperation_Unpin : Translator.FolderOperation_Pin, UIAlertActionStyle.Default,
                _ => Observe(ViewModel.ChangePinnedStatusAsync(items, !allPinned))));
            sheet.AddAction(UIAlertAction.Create(Translator.Buttons_Cancel, UIAlertActionStyle.Cancel, null));
            if (sheet.PopoverPresentationController is { } popover && IndexPathOf(row) is { } indexPath && _table.CellAt(indexPath) is { } cell)
            {
                popover.SourceView = cell;
                popover.SourceRect = cell.Bounds;
            }
            PresentViewController(sheet, true, null);
        });
        action.BackgroundColor = UIColor.SystemGray;
        action.Image = WinoIcons.TemplateImage(WinoIconGlyph.More, 20);
        return action;
    }

    // ---- Long-press menu (Windows MailContextFlyoutBuilder, Mac BuildContextMenu) ----

    private UIContextMenuConfiguration? ContextMenu(NSIndexPath indexPath)
    {
        if (RowAt(indexPath) is not { } row) return null;
        var targets = Targets(row);
        if (targets.Count == 0) return null;
        var composeTarget = row.SourceItem as MailItemViewModel;
        return UIContextMenuConfiguration.Create(null, null, _ =>
        {
            var elements = new List<UIMenuElement>(SelectionMenuElements(targets, composeTarget, includeQuickActions: true));
            if (row.IsThreadHead && row.Thread is { Count: > 1 })
                elements.Add(PhoneBarItems.Inline(PhoneBarItems.Action(row.IsExpanded ? Translator.MacOS_MailList_CollapseThread : Translator.MacOS_MailList_ExpandThread,
                    row.IsExpanded ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown, () => ToggleThread(row))));
            return UIMenu.Create(elements.ToArray());
        });
    }

    /// <summary>
    /// The shared actions for <paramref name="targets"/> grouped at the separators, then Pin and the Category submenu.
    /// The edit toolbar's More menu leaves out the actions the toolbar already shows.
    /// </summary>
    private UIMenuElement[] SelectionMenuElements(IReadOnlyList<MailItemViewModel> targets, MailItemViewModel? composeTarget = null, bool includeQuickActions = false)
    {
        if (targets.Count == 0) return [];
        var elements = new List<UIMenuElement>();
        var group = new List<UIMenuElement>();
        foreach (var available in ViewModel.GetAvailableMailActions(targets))
        {
            if (available.Operation == MailOperation.Seperator)
            {
                if (group.Count > 0) elements.Add(PhoneBarItems.Inline(group.ToArray()));
                group = [];
                continue;
            }
            if (!includeQuickActions && available.Operation is MailOperation.Archive or MailOperation.UnArchive or MailOperation.SoftDelete
                or MailOperation.SetFlag or MailOperation.ClearFlag or MailOperation.Move) continue;
            var title = MailOperationDisplay.GetTitle(available.Operation);
            if (string.IsNullOrEmpty(title)) continue;
            var operation = available.Operation;
            group.Add(PhoneBarItems.Action(operation == MailOperation.Move ? $"{title}…" : title, MailOperationDisplay.GetGlyph(operation),
                () => RunOperation(operation, targets, composeTarget), available.IsEnabled,
                operation is MailOperation.SoftDelete or MailOperation.HardDelete));
        }
        if (group.Count > 0) elements.Add(PhoneBarItems.Inline(group.ToArray()));

        bool allPinned = targets.All(static target => target.MailCopy.IsPinned);
        var pin = PhoneBarItems.Action(allPinned ? Translator.FolderOperation_Unpin : Translator.FolderOperation_Pin, allPinned ? WinoIconGlyph.UnPin : WinoIconGlyph.Pin,
            () => Observe(ViewModel.ChangePinnedStatusAsync(targets, !allPinned)));
        elements.Add(PhoneBarItems.Inline(pin, CategoryMenu(targets)));
        return elements.ToArray();
    }

    /// <summary>The Category submenu; categories load when it opens and it says so while there are none.</summary>
    private UIMenu CategoryMenu(IReadOnlyList<MailItemViewModel> targets)
    {
        var deferred = UIDeferredMenuElement.CreateUncached(completion => Observe(LoadCategoriesAsync(targets, completion)));
        return UIMenu.Create(Translator.MailCategoryMenuItem, WinoIcons.TemplateImage(WinoIconGlyph.SpecialFolderCategory, 18), UIMenuIdentifier.None,
            default, [deferred]);
    }

    private async Task LoadCategoriesAsync(IReadOnlyList<MailItemViewModel> targets, UIDeferredMenuElementCompletionHandler completion)
    {
        IReadOnlyList<MailCategory> categories = Array.Empty<MailCategory>();
        IReadOnlyCollection<Guid> assigned = Array.Empty<Guid>();
        try { (categories, assigned) = await ViewModel.GetAvailableCategoriesAsync(targets); }
        catch (Exception exception) { ReportError(exception); }
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            // No categories (or a mixed-account selection): the submenu stays empty, like the Mac's hidden item.
            if (categories.Count == 0)
            {
                completion([]);
                return;
            }
            var favorites = categories.Where(static category => category.IsFavorite).Select(category => CategoryAction(category, assigned, targets)).ToArray();
            var others = categories.Where(static category => !category.IsFavorite).Select(category => CategoryAction(category, assigned, targets)).ToArray();
            var elements = new List<UIMenuElement>();
            if (favorites.Length > 0) elements.Add(PhoneBarItems.Inline(favorites));
            if (others.Length > 0) elements.Add(PhoneBarItems.Inline(others));
            completion(elements.ToArray());
        });
    }

    private UIMenuElement CategoryAction(MailCategory category, IReadOnlyCollection<Guid> assigned, IReadOnlyList<MailItemViewModel> targets)
    {
        bool assignedToAll = assigned.Contains(category.Id);
        var color = WinoStyle.FromHexString(category.BackgroundColorHex);
        var image = color is null
            ? WinoIcons.TemplateImage(WinoIconGlyph.SpecialFolderCategory, 18)
            : new UIGraphicsImageRenderer(new CoreGraphics.CGSize(14, 14)).CreateImage(_ =>
            {
                color.SetFill();
                UIBezierPath.FromOval(new CoreGraphics.CGRect(1, 1, 12, 12)).Fill();
            }).ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
        var action = UIAction.Create(category.Name ?? string.Empty, image, null, _ => Observe(ViewModel.ToggleCategoryAssignmentAsync(category, targets, assignedToAll)));
        action.State = assignedToAll ? UIMenuElementState.On : UIMenuElementState.Off;
        return action;
    }

    // ---- Move (a sheet-presented folder picker over the shared folder structure) ----

    private void ShowMoveSheet(IReadOnlyList<MailItemViewModel> items)
    {
        var mails = items.Select(static item => item.MailCopy).ToArray();
        if (!TryShowMove(mails))
        {
            // Mixed accounts: the shared path picks the folder (or reports the problem).
            if (mails.Length > 0) Observe(ViewModel.ExecuteMailOperationAsync(new MailOperationPreperationRequest(MailOperation.Move, mails)));
        }
    }

    /// <summary>
    /// Opens the Move sheet for <paramref name="mails"/> (the list's Move and, through <see cref="PhoneMailMovePresenter"/>,
    /// the reader toolbar's Move). False when the mails span accounts or there are none.
    /// </summary>
    internal bool TryShowMove(IReadOnlyList<MailCopy> mails)
    {
        if (_released || mails.Count == 0) return false;
        var accountIds = mails.Select(static mail => mail.AssignedAccount?.Id).Distinct().ToArray();
        if (accountIds.Length != 1 || accountIds[0] is not Guid accountId) return false;
        Observe(ShowMoveSheetAsync(mails, accountId));
        return true;
    }

    private async Task ShowMoveSheetAsync(IReadOnlyList<MailCopy> mails, Guid accountId)
    {
        IReadOnlyList<IMailItemFolder> folders = await _folderService.GetFolderStructureForDisplayAsync(accountId);
        var sourceFolderIds = mails.Select(static mail => mail.AssignedFolder?.Id).OfType<Guid>().ToHashSet();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (_released) return;
            DismissMoveSheet();
            var picker = new MoveFolderSheetViewController(folders, sourceFolderIds, folder =>
            {
                DismissMoveSheet();
                Observe(ViewModel.ExecuteMailOperationAsync(new MailOperationPreperationRequest(MailOperation.Move, mails, moveTargetFolder: folder)));
                if (_selectionMode) ViewModel.IsMultiSelectionModeEnabled = false;
            }, DismissMoveSheet);
            _moveSheet = _host.PresentSheet(picker, PhoneSheetSize.MediumAndLarge, () => _moveSheet = null);
        });
    }

    private void DismissMoveSheet()
    {
        if (_moveSheet is not { } sheet) return;
        _moveSheet = null;
        _ = _host.DismissSheetAsync(sheet);
    }

    private void DismissOwnSheets()
    {
        DismissMoveSheet();
        DismissFilterSheet();
    }
}
