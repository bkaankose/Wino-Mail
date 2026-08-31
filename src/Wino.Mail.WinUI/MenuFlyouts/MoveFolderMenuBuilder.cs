using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Models.Folders;

namespace Wino.MenuFlyouts;

public static class MoveFolderMenuBuilder
{
    public static int Populate(
        IList<MenuFlyoutItemBase> destination,
        IEnumerable<IMailItemFolder> folders,
        IReadOnlySet<Guid> sourceFolderIds,
        Action<IMailItemFolder> folderClicked)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(sourceFolderIds);
        ArgumentNullException.ThrowIfNull(folderClicked);

        var validTargetCount = 0;

        foreach (var folder in folders)
        {
            if (folder == null)
                continue;

            var (menuItem, nestedValidTargetCount) = CreateFolderMenuItem(folder, sourceFolderIds, folderClicked);

            destination.Add(menuItem);
            validTargetCount += nestedValidTargetCount;
        }

        return validTargetCount;
    }

    private static (MenuFlyoutItemBase MenuItem, int ValidTargetCount) CreateFolderMenuItem(
        IMailItemFolder folder,
        IReadOnlySet<Guid> sourceFolderIds,
        Action<IMailItemFolder> folderClicked)
    {
        var isValidTarget = folder.IsMoveTarget && !sourceFolderIds.Contains(folder.Id);
        var hasChildren = folder.ChildFolders?.Count > 0;
        var automationId = $"MoveFolder_{folder.Id:N}";

        if (!hasChildren)
        {
            var leafItem = new MenuFlyoutItem
            {
                Text = folder.FolderName ?? string.Empty,
                IsEnabled = isValidTarget,
                Tag = folder
            };

            AutomationProperties.SetAutomationId(leafItem, automationId);

            if (isValidTarget)
            {
                leafItem.Click += (_, _) => folderClicked(folder);
            }

            return (leafItem, isValidTarget ? 1 : 0);
        }

        if (!isValidTarget)
        {
            var structuralItem = new MenuFlyoutSubItem
            {
                Text = folder.FolderName ?? string.Empty,
                Tag = folder
            };

            AutomationProperties.SetAutomationId(structuralItem, automationId);

            var validChildCount = Populate(
                structuralItem.Items,
                folder.ChildFolders,
                sourceFolderIds,
                folderClicked);

            return (structuralItem, validChildCount);
        }

        var splitItem = new SplitMenuFlyoutItem
        {
            Text = folder.FolderName ?? string.Empty,
            Tag = folder
        };

        AutomationProperties.SetAutomationId(splitItem, automationId);
        splitItem.Click += (_, _) => folderClicked(folder);

        var nestedTargetCount = Populate(
            splitItem.Items,
            folder.ChildFolders,
            sourceFolderIds,
            folderClicked);

        return (splitItem, nestedTargetCount + 1);
    }
}
