using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Menus;
using Wino.Helpers;
using Wino.Mail.Controls.Core.ContextFlyout;
using Wino.Mail.WinUI.Controls;

namespace Wino.MenuFlyouts;

/// <summary>
/// Builds the mail context menu as a plain definition. Nothing here knows how the flyout renders
/// the menu; the control resolves every visual from these entries.
/// </summary>
internal static class MailContextFlyoutBuilder
{
    private static readonly MailOperation[] HeaderOperations =
    [
        MailOperation.Reply,
        MailOperation.ReplyAll,
        MailOperation.Forward
    ];

    public static MailContextFlyoutMenu Build(
        IEnumerable<MailOperationMenuItem> availableActions,
        IReadOnlyList<MailCategory> availableCategories,
        IReadOnlyCollection<Guid> assignedCategoryIds,
        IReadOnlyList<IMailItemFolder>? moveFolders,
        IReadOnlySet<Guid> sourceFolderIds,
        bool areAllPinned,
        IKeyboardShortcutService shortcutService,
        Action<MailContextFlyoutSelection> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);

        var items = new List<ContextFlyoutMenuEntry>();
        var actions = availableActions?.ToList() ?? [];
        var shortcutResolver = new MailContextFlyoutShortcutResolver(shortcutService);
        var focusedInboxActions = actions.Where(action => IsFocusedInboxMoveOperation(action.Operation)).ToList();

        foreach (var action in actions)
        {
            if (HeaderOperations.Contains(action.Operation))
            {
                continue;
            }

            if (action.Operation == MailOperation.Seperator)
            {
                AddSeparatorIfNeeded(items);
                continue;
            }

            if (IsFocusedInboxMoveOperation(action.Operation))
            {
                continue;
            }

            if (action.Operation == MailOperation.Move)
            {
                items.Add(CreateMoveItem(
                    action,
                    focusedInboxActions,
                    moveFolders,
                    sourceFolderIds,
                    shortcutResolver,
                    selected));
                continue;
            }

            items.Add(CreateOperationItem(action, shortcutResolver, selected));
        }

        AddSeparatorIfNeeded(items);
        items.Add(new ContextFlyoutCommandEntry
        {
            Text = areAllPinned ? Translator.FolderOperation_Unpin : Translator.FolderOperation_Pin,
            Icon = CreateIcon(areAllPinned ? WinoIconGlyph.UnPin : WinoIconGlyph.Pin),
            Command = CreateCommand(() => selected(new MailContextFlyoutSelection(PinState: !areAllPinned))),
            AutomationId = "MailContextPin"
        });

        if (availableCategories?.Count > 0)
        {
            AddSeparatorIfNeeded(items);
            items.Add(CreateCategoriesItem(availableCategories, assignedCategoryIds, selected));
        }

#if DEBUG
        AddSeparatorIfNeeded(items);
        items.Add(new ContextFlyoutCommandEntry
        {
            Text = Translator.Buttons_TestNotification,
            Command = CreateCommand(() => selected(new MailContextFlyoutSelection(CreateTestNotification: true))),
            AutomationId = "MailContextTestNotification"
        });
#endif

        return new MailContextFlyoutMenu(items, CreateHeaderItems(actions, shortcutResolver, selected));
    }

    private static IReadOnlyList<ContextFlyoutHeaderEntry> CreateHeaderItems(
        IReadOnlyList<MailOperationMenuItem> actions,
        MailContextFlyoutShortcutResolver shortcutResolver,
        Action<MailContextFlyoutSelection> selected)
    {
        var headerItems = new List<ContextFlyoutHeaderEntry>();

        foreach (var operation in HeaderOperations)
        {
            var action = actions.FirstOrDefault(candidate => candidate.Operation == operation);
            var isEnabled = action?.IsEnabled == true;

            headerItems.Add(new ContextFlyoutHeaderEntry
            {
                Label = XamlHelpers.GetOperationString(operation),
                Icon = CreateIcon(XamlHelpers.GetWinoIconGlyph(operation)),
                IsEnabled = isEnabled,
                Command = action is null
                    ? null
                    : CreateCommand(() => selected(new MailContextFlyoutSelection(Operation: action)), isEnabled),
                Shortcut = isEnabled ? shortcutResolver.Resolve(operation) : null,
                AutomationId = $"MailContextHeader{operation}"
            });
        }

        return headerItems;
    }

    private static ContextFlyoutCommandEntry CreateOperationItem(
        MailOperationMenuItem action,
        MailContextFlyoutShortcutResolver shortcutResolver,
        Action<MailContextFlyoutSelection> selected)
        => new()
        {
            Text = XamlHelpers.GetOperationString(action.Operation),
            Icon = CreateIcon(XamlHelpers.GetWinoIconGlyph(action.Operation)),
            IsEnabled = action.IsEnabled,
            IsDestructive = action.Operation is MailOperation.SoftDelete or MailOperation.HardDelete or MailOperation.DiscardLocalDraft,
            Command = CreateCommand(() => selected(new MailContextFlyoutSelection(Operation: action)), action.IsEnabled),
            Shortcut = shortcutResolver.Resolve(action.Operation),
            AutomationId = $"MailContextOperation_{action.Operation}"
        };

    private static ContextFlyoutSubMenuEntry CreateMoveItem(
        MailOperationMenuItem moveAction,
        IReadOnlyList<MailOperationMenuItem> focusedInboxActions,
        IReadOnlyList<IMailItemFolder>? moveFolders,
        IReadOnlySet<Guid> sourceFolderIds,
        MailContextFlyoutShortcutResolver shortcutResolver,
        Action<MailContextFlyoutSelection> selected)
    {
        var children = new List<ContextFlyoutMenuEntry>();

        foreach (var focusedInboxAction in focusedInboxActions)
        {
            children.Add(CreateOperationItem(focusedInboxAction, shortcutResolver, selected));
        }

        var folderItems = new List<ContextFlyoutMenuEntry>();
        if (moveAction.IsEnabled && moveFolders is not null)
        {
            AddMoveFolderItems(folderItems, moveFolders, sourceFolderIds, moveAction, selected);
        }

        if (children.Count > 0 && folderItems.Count > 0)
        {
            AddSeparatorIfNeeded(children);
        }

        children.AddRange(folderItems);

        return new ContextFlyoutSubMenuEntry
        {
            Text = XamlHelpers.GetOperationString(MailOperation.Move),
            Icon = CreateIcon(XamlHelpers.GetWinoIconGlyph(MailOperation.Move)),
            IsEnabled = children.Any(CanActivate),
            Items = children,
            AutomationId = "MailContextMove"
        };
    }

    private static void AddMoveFolderItems(
        IList<ContextFlyoutMenuEntry> destination,
        IEnumerable<IMailItemFolder> folders,
        IReadOnlySet<Guid> sourceFolderIds,
        MailOperationMenuItem moveAction,
        Action<MailContextFlyoutSelection> selected)
    {
        foreach (var folder in folders)
        {
            if (folder is null)
            {
                continue;
            }

            var folderName = folder.FolderName ?? string.Empty;
            var childItems = new List<ContextFlyoutMenuEntry>();
            if (folder.ChildFolders?.Count > 0)
            {
                AddMoveFolderItems(childItems, folder.ChildFolders, sourceFolderIds, moveAction, selected);
            }

            var isValidTarget = !string.IsNullOrWhiteSpace(folderName)
                && folder.IsMoveTarget
                && !sourceFolderIds.Contains(folder.Id);

            if (string.IsNullOrWhiteSpace(folderName))
            {
                foreach (var childItem in childItems)
                {
                    destination.Add(childItem);
                }

                continue;
            }

            if (childItems.Count == 0)
            {
                if (isValidTarget)
                {
                    destination.Add(CreateMoveFolderCommand(folderName, folder, moveAction, selected));
                }

                continue;
            }

            var groupItems = new List<ContextFlyoutMenuEntry>();

            if (isValidTarget)
            {
                groupItems.Add(CreateMoveFolderCommand(
                    string.Format(Translator.DragMoveToFolderCaption, folderName),
                    folder,
                    moveAction,
                    selected));
                AddSeparatorIfNeeded(groupItems);
            }

            groupItems.AddRange(childItems);

            destination.Add(new ContextFlyoutSubMenuEntry
            {
                Text = folderName,
                SearchKeywords = "folder destination",
                Icon = CreateIcon(XamlHelpers.GetSpecialFolderPathIconGeometry(folder.SpecialFolderType)),
                Items = groupItems,
                AutomationId = $"MoveFolderGroup_{folder.Id:N}"
            });
        }
    }

    private static ContextFlyoutCommandEntry CreateMoveFolderCommand(
        string text,
        IMailItemFolder folder,
        MailOperationMenuItem moveAction,
        Action<MailContextFlyoutSelection> selected)
        => new()
        {
            Text = text,
            SearchKeywords = $"folder destination {folder.FolderName}",
            Icon = CreateIcon(XamlHelpers.GetSpecialFolderPathIconGeometry(folder.SpecialFolderType)),
            Command = CreateCommand(() => selected(new MailContextFlyoutSelection(
                Operation: moveAction,
                MoveTargetFolder: folder))),
            AutomationId = $"MoveFolder_{folder.Id:N}"
        };

    private static ContextFlyoutSubMenuEntry CreateCategoriesItem(
        IReadOnlyList<MailCategory> availableCategories,
        IReadOnlyCollection<Guid> assignedCategoryIds,
        Action<MailContextFlyoutSelection> selected)
    {
        var categoryItems = new List<ContextFlyoutMenuEntry>();
        var favoriteCategories = availableCategories.Where(category => category.IsFavorite).ToList();
        var remainingCategories = availableCategories.Where(category => !category.IsFavorite).ToList();

        AddCategoryItems(categoryItems, favoriteCategories, assignedCategoryIds, selected);

        if (favoriteCategories.Count > 0 && remainingCategories.Count > 0)
        {
            AddSeparatorIfNeeded(categoryItems);
        }

        AddCategoryItems(categoryItems, remainingCategories, assignedCategoryIds, selected);

        return new ContextFlyoutSubMenuEntry
        {
            Text = Translator.MailCategoryMenuItem,
            Icon = CreateIcon(WinoIconGlyph.SpecialFolderCategory),
            Items = categoryItems,
            AutomationId = "MailContextCategories"
        };
    }

    private static void AddCategoryItems(
        IList<ContextFlyoutMenuEntry> destination,
        IEnumerable<MailCategory> categories,
        IReadOnlyCollection<Guid> assignedCategoryIds,
        Action<MailContextFlyoutSelection> selected)
    {
        foreach (var category in categories)
        {
            var wasAssignedToAll = assignedCategoryIds.Contains(category.Id);

            destination.Add(new ContextFlyoutToggleEntry
            {
                Text = category.Name,
                SearchKeywords = category.Name,
                Icon = CreateIcon(WinoIconGlyph.SpecialFolderCategory, category.TextColorHex),
                IsChecked = wasAssignedToAll,
                Command = CreateCommand(() => selected(new MailContextFlyoutSelection(
                    Category: category,
                    IsCategoryAssignedToAll: wasAssignedToAll))),
                AutomationId = $"MailContextCategory_{category.Id:N}"
            });
        }
    }

    private static bool CanActivate(ContextFlyoutMenuEntry entry)
        => entry switch
        {
            ContextFlyoutSubMenuEntry subMenu => subMenu.IsEnabled && subMenu.Items.Count > 0,
            ContextFlyoutCommandEntry command => command.CanExecute(),
            _ => false
        };

    private static ICommand CreateCommand(Action execute, bool canExecute = true)
        => new RelayCommand(execute, () => canExecute);

    private static bool IsFocusedInboxMoveOperation(MailOperation operation)
        => operation is MailOperation.MoveToFocused
            or MailOperation.MoveToOther
            or MailOperation.AlwaysMoveToFocused
            or MailOperation.AlwaysMoveToOther;

    private static void AddSeparatorIfNeeded(IList<ContextFlyoutMenuEntry> destination)
    {
        if (destination.Count > 0 && destination[^1] is not ContextFlyoutSeparatorEntry)
        {
            destination.Add(ContextFlyoutSeparatorEntry.Instance);
        }
    }

    private static ContextFlyoutIcon? CreateIcon(WinoIconGlyph icon, string? foregroundHex = null)
        => ControlConstants.WinoIconFontDictionary.TryGetValue(icon, out var glyph)
            ? new ContextFlyoutIcon(glyph, foregroundHex)
            : null;
}

internal sealed record MailContextFlyoutMenu(
    IReadOnlyList<ContextFlyoutMenuEntry> Items,
    IReadOnlyList<ContextFlyoutHeaderEntry> HeaderItems);

internal sealed record MailContextFlyoutSelection(
    MailOperationMenuItem? Operation = null,
    MailCategory? Category = null,
    bool IsCategoryAssignedToAll = false,
    bool? PinState = null,
    bool CreateTestNotification = false,
    IMailItemFolder? MoveTargetFolder = null);
