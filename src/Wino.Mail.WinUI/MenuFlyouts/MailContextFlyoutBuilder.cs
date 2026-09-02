using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Wino.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Menus;
using Wino.Helpers;
using Wino.Mail.Controls.ContextFlyout;
using Wino.Mail.WinUI.Controls;

namespace Wino.MenuFlyouts;

internal static class MailContextFlyoutBuilder
{
    public static void Populate(
        IList<DependencyObject> destination,
        IEnumerable<MailOperationMenuItem> availableActions,
        IReadOnlyList<MailCategory> availableCategories,
        IReadOnlyCollection<Guid> assignedCategoryIds,
        IReadOnlyList<IMailItemFolder>? moveFolders,
        IReadOnlySet<Guid> sourceFolderIds,
        bool areAllPinned,
        IKeyboardShortcutService shortcutService,
        Action<MailContextFlyoutSelection> selected)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(selected);

        var actions = availableActions?.ToList() ?? [];
        var shortcutResolver = new MailContextFlyoutShortcutResolver(shortcutService);
        var focusedInboxActions = actions.Where(action => IsFocusedInboxMoveOperation(action.Operation)).ToList();

        foreach (var action in actions)
        {
            if (action.Operation == MailOperation.Seperator)
            {
                AddSeparatorIfNeeded(destination);
                continue;
            }

            if (IsFocusedInboxMoveOperation(action.Operation))
            {
                continue;
            }

            if (action.Operation == MailOperation.Move)
            {
                AddMoveItems(
                    destination,
                    action,
                    focusedInboxActions,
                    moveFolders,
                    sourceFolderIds,
                    shortcutResolver,
                    selected);
                continue;
            }

            destination.Add(CreateOperationItem(action, shortcutResolver, selected));
        }

        AddSeparatorIfNeeded(destination);
        destination.Add(new WinoContextFlyoutItem
        {
            Text = areAllPinned ? Translator.FolderOperation_Unpin : Translator.FolderOperation_Pin,
            IconSource = new WinoFontIconSource { Icon = areAllPinned ? WinoIconGlyph.UnPin : WinoIconGlyph.Pin },
            Command = CreateCommand(() => selected(new MailContextFlyoutSelection(PinState: !areAllPinned))),
            AutomationId = "MailContextPin"
        });

        if (availableCategories?.Count > 0)
        {
            AddSeparatorIfNeeded(destination);

            var favoriteCategories = availableCategories.Where(category => category.IsFavorite).ToList();
            var remainingCategories = availableCategories.Where(category => !category.IsFavorite).ToList();

            AddCategoryItems(destination, favoriteCategories, assignedCategoryIds, selected);

            if (favoriteCategories.Count > 0 && remainingCategories.Count > 0)
            {
                AddSeparatorIfNeeded(destination);
            }

            AddCategoryItems(destination, remainingCategories, assignedCategoryIds, selected);
        }

#if DEBUG
        AddSeparatorIfNeeded(destination);
        destination.Add(new WinoContextFlyoutItem
        {
            Text = Translator.Buttons_TestNotification,
            Command = CreateCommand(() => selected(new MailContextFlyoutSelection(CreateTestNotification: true))),
            AutomationId = "MailContextTestNotification"
        });
#endif
    }

    private static WinoContextFlyoutItem CreateOperationItem(
        MailOperationMenuItem action,
        MailContextFlyoutShortcutResolver shortcutResolver,
        Action<MailContextFlyoutSelection> selected,
        string breadcrumb = "")
    {
        var shortcut = shortcutResolver.Resolve(action.Operation);
        var operationText = XamlHelpers.GetOperationString(action.Operation);
        var displayText = string.IsNullOrWhiteSpace(breadcrumb)
            ? operationText
            : $"{breadcrumb} › {operationText}";

        return new WinoContextFlyoutItem
        {
            Text = displayText,
            Breadcrumb = breadcrumb,
            IconSource = new WinoFontIconSource { Icon = XamlHelpers.GetWinoIconGlyph(action.Operation) },
            IsEnabled = action.IsEnabled,
            IsDestructive = action.Operation is MailOperation.SoftDelete or MailOperation.HardDelete or MailOperation.DiscardLocalDraft,
            Command = CreateCommand(() => selected(new MailContextFlyoutSelection(Operation: action)), action.IsEnabled),
            ShortcutText = shortcut?.DisplayText ?? string.Empty,
            KeyboardAccelerator = shortcut?.Accelerator,
            AutomationId = $"MailContextOperation_{action.Operation}"
        };
    }

    private static void AddMoveItems(
        IList<DependencyObject> destination,
        MailOperationMenuItem moveAction,
        IReadOnlyList<MailOperationMenuItem> focusedInboxActions,
        IReadOnlyList<IMailItemFolder>? moveFolders,
        IReadOnlySet<Guid> sourceFolderIds,
        MailContextFlyoutShortcutResolver shortcutResolver,
        Action<MailContextFlyoutSelection> selected)
    {
        var moveText = XamlHelpers.GetOperationString(MailOperation.Move);

        foreach (var focusedInboxAction in focusedInboxActions)
        {
            destination.Add(CreateOperationItem(focusedInboxAction, shortcutResolver, selected, moveText));
        }

        if (moveFolders is null || !moveAction.IsEnabled)
        {
            return;
        }

        if (focusedInboxActions.Count > 0 && moveFolders.Count > 0)
        {
            AddSeparatorIfNeeded(destination);
        }

        AddMoveFolders(destination, moveFolders, sourceFolderIds, moveText, moveAction, selected);
    }

    private static void AddMoveFolders(
        IList<DependencyObject> destination,
        IEnumerable<IMailItemFolder> folders,
        IReadOnlySet<Guid> sourceFolderIds,
        string breadcrumb,
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
            var folderPath = string.IsNullOrWhiteSpace(folderName)
                ? breadcrumb
                : string.IsNullOrWhiteSpace(breadcrumb)
                    ? folderName
                    : $"{breadcrumb} › {folderName}";

            if (!string.IsNullOrWhiteSpace(folderName)
                && folder.IsMoveTarget
                && !sourceFolderIds.Contains(folder.Id))
            {
                destination.Add(new WinoContextFlyoutItem
                {
                    Text = folderPath,
                    Breadcrumb = breadcrumb,
                    SearchKeywords = $"folder destination {folderName}",
                    IconSource = new WinoFontIconSource
                    {
                        Icon = XamlHelpers.GetSpecialFolderPathIconGeometry(folder.SpecialFolderType)
                    },
                    Command = CreateCommand(() => selected(new MailContextFlyoutSelection(
                        Operation: moveAction,
                        MoveTargetFolder: folder))),
                    AutomationId = $"MoveFolder_{folder.Id:N}"
                });
            }

            if (folder.ChildFolders?.Count > 0)
            {
                AddMoveFolders(destination, folder.ChildFolders, sourceFolderIds, folderPath, moveAction, selected);
            }
        }
    }

    private static ICommand CreateCommand(Action execute, bool canExecute = true)
        => new RelayCommand(execute, () => canExecute);

    private static void AddCategoryItems(
        IList<DependencyObject> destination,
        IEnumerable<MailCategory> categories,
        IReadOnlyCollection<Guid> assignedCategoryIds,
        Action<MailContextFlyoutSelection> selected)
    {
        foreach (var category in categories)
        {
            var wasAssignedToAll = assignedCategoryIds.Contains(category.Id);
            destination.Add(new WinoContextFlyoutToggleItem
            {
                Text = $"{Translator.MailCategoryMenuItem} › {category.Name}",
                Breadcrumb = Translator.MailCategoryMenuItem,
                SearchKeywords = category.Name,
                IconSource = new SymbolIconSource
                {
                    Symbol = Symbol.Tag,
                    Foreground = XamlHelpers.GetSolidColorBrushFromHex(category.TextColorHex)
                },
                IsChecked = wasAssignedToAll,
                Command = CreateCommand(() => selected(new MailContextFlyoutSelection(
                    Category: category,
                    IsCategoryAssignedToAll: wasAssignedToAll))),
                AutomationId = $"MailContextCategory_{category.Id:N}"
            });
        }
    }

    private static bool IsFocusedInboxMoveOperation(MailOperation operation)
        => operation is MailOperation.MoveToFocused
            or MailOperation.MoveToOther
            or MailOperation.AlwaysMoveToFocused
            or MailOperation.AlwaysMoveToOther;

    private static void AddSeparatorIfNeeded(IList<DependencyObject> destination)
    {
        if (destination.Count > 0 && destination[^1] is not WinoContextFlyoutSeparator)
        {
            destination.Add(new WinoContextFlyoutSeparator());
        }
    }
}

internal sealed record MailContextFlyoutSelection(
    MailOperationMenuItem? Operation = null,
    MailCategory? Category = null,
    bool IsCategoryAssignedToAll = false,
    bool? PinState = null,
    bool CreateTestNotification = false,
    IMailItemFolder? MoveTargetFolder = null);
