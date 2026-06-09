using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Folders;
using Wino.Services;

namespace Wino.Mail.ViewModels.Helpers;

/// <summary>
/// Composes navigation menu items for account folders on the UI side.
/// Folder and category data is fetched through serializable service calls so the
/// services can live in the background companion process; only the IMenuItem
/// composition (a pure UI concern) happens here.
/// </summary>
public static class FolderMenuItemFactory
{
    public static Task<IEnumerable<IMenuItem>> GetAccountFoldersForDisplayAsync(IFolderService folderService,
                                                                                IMailCategoryService mailCategoryService,
                                                                                IAccountMenuItem accountMenuItem)
    {
        if (accountMenuItem is IMergedAccountMenuItem mergedAccountFolderMenuItem)
        {
            return GetMergedAccountFolderMenuItemsAsync(folderService, mailCategoryService, mergedAccountFolderMenuItem);
        }
        else
        {
            return GetSingleAccountFolderMenuItemsAsync(folderService, mailCategoryService, accountMenuItem);
        }
    }

    private static FolderMenuItem GetPreparedFolderMenuItemRecursive(MailAccount account,
                                                                     MailItemFolder parentFolder,
                                                                     IMenuItem parentMenuItem,
                                                                     IReadOnlyList<MailItemFolder> allAccountFolders)
    {
        // Localize category folder name.
        if (parentFolder.SpecialFolderType == SpecialFolderType.Category) parentFolder.FolderName = Translator.CategoriesFolderNameOverride;

        var preparedFolder = new FolderMenuItem(parentFolder, account, parentMenuItem);

        // SQL 'ParentRemoteFolderId = ?' never matches NULL; preserve that semantic here.
        if (!string.IsNullOrEmpty(parentFolder.RemoteFolderId))
        {
            var childFolders = allAccountFolders
                .Where(a => !string.IsNullOrEmpty(a.ParentRemoteFolderId)
                            && a.ParentRemoteFolderId == parentFolder.RemoteFolderId
                            && a.MailAccountId == parentFolder.MailAccountId);

            foreach (var subChildFolder in childFolders)
            {
                var preparedChild = GetPreparedFolderMenuItemRecursive(account, subChildFolder, preparedFolder, allAccountFolders);

                if (preparedChild == null) continue;

                preparedFolder.SubMenuItems.Add(preparedChild);
            }
        }

        return preparedFolder;
    }

    private static async Task<IEnumerable<IMenuItem>> GetSingleAccountFolderMenuItemsAsync(IFolderService folderService,
                                                                                           IMailCategoryService mailCategoryService,
                                                                                           IAccountMenuItem accountMenuItem)
    {
        var accountId = accountMenuItem.EntityId.Value;
        var preparedFolderMenuItems = new List<IMenuItem>();

        // Get all folders for the account. Excluding hidden folders.
        var folders = await folderService.GetVisibleFoldersAsync(accountId).ConfigureAwait(false);

        if (!folders.Any()) return new List<IMenuItem>();

        // Hidden folders can still appear as children of visible ones, matching the previous behavior.
        var allAccountFolders = await folderService.GetFoldersAsync(accountId).ConfigureAwait(false);

        var mailAccount = accountMenuItem.HoldingAccounts.First();

        var moreFolder = MailItemFolder.CreateMoreFolder();
        var categoryFolder = MailItemFolder.CreateCategoriesFolder();

        var moreFolderMenuItem = new FolderMenuItem(moreFolder, mailAccount, accountMenuItem);
        var categoryFolderMenuItem = new FolderMenuItem(categoryFolder, mailAccount, accountMenuItem);

        foreach (var item in folders)
        {
            // Category type folders should be skipped. They will be categorized under virtual category folder.
            if (ServiceConstants.SubCategoryFolderLabelIds.Contains(item.RemoteFolderId)) continue;

            bool skipEmptyParentRemoteFolders = mailAccount.ProviderType == MailProviderType.Gmail;

            if (skipEmptyParentRemoteFolders && !string.IsNullOrEmpty(item.ParentRemoteFolderId)) continue;

            // Sticky items belong to account menu item directly. Rest goes to More folder.
            IMenuItem parentFolderMenuItem = item.IsSticky ? accountMenuItem : ServiceConstants.SubCategoryFolderLabelIds.Contains(item.FolderName.ToUpper()) ? categoryFolderMenuItem : moreFolderMenuItem;

            var preparedItem = GetPreparedFolderMenuItemRecursive(mailAccount, item, parentFolderMenuItem, allAccountFolders);

            // Don't add menu items that are prepared for More folder. They've been included in More virtual folder already.
            // We'll add More folder later on at the end of the list.

            if (preparedItem == null) continue;

            if (item.IsSticky)
            {
                preparedFolderMenuItems.Add(preparedItem);
            }
            else if (parentFolderMenuItem is FolderMenuItem baseParentFolderMenuItem)
            {
                baseParentFolderMenuItem.SubMenuItems.Add(preparedItem);
            }
        }

        var favoriteCategories = await GetFavoriteCategoryMenuItemsAsync(mailCategoryService, mailAccount, folders, accountMenuItem).ConfigureAwait(false);
        preparedFolderMenuItems.AddRange(favoriteCategories);

        // Only add category folder if it's Gmail.
        if (mailAccount.ProviderType == MailProviderType.Gmail) preparedFolderMenuItems.Add(categoryFolderMenuItem);

        // Only add More folder if there are any items in it.
        if (moreFolderMenuItem.SubMenuItems.Any()) preparedFolderMenuItems.Add(moreFolderMenuItem);

        return preparedFolderMenuItems;
    }

    private static async Task<IEnumerable<IMenuItem>> GetMergedAccountFolderMenuItemsAsync(IFolderService folderService,
                                                                                           IMailCategoryService mailCategoryService,
                                                                                           IMergedAccountMenuItem mergedAccountFolderMenuItem)
    {
        var holdingAccounts = mergedAccountFolderMenuItem.HoldingAccounts;

        if (holdingAccounts == null || !holdingAccounts.Any()) return [];

        var preparedFolderMenuItems = new List<IMenuItem>();

        // First gather all account folders.
        // Prepare single menu items for both of them.

        var allAccountFolders = new List<List<MailItemFolder>>();

        foreach (var account in holdingAccounts)
        {
            var accountFolders = await folderService.GetVisibleFoldersAsync(account.Id).ConfigureAwait(false);

            allAccountFolders.Add(accountFolders);
        }

        var commonFolders = FindCommonFolders(allAccountFolders);

        // Prepare menu items for common folders.
        foreach (var commonFolderType in commonFolders)
        {
            var folderItems = allAccountFolders.SelectMany(a => a.Where(b => b.SpecialFolderType == commonFolderType)).Cast<IMailItemFolder>().ToList();
            var menuItem = new MergedAccountFolderMenuItem(folderItems, null, mergedAccountFolderMenuItem.Parameter);

            preparedFolderMenuItems.Add(menuItem);
        }

        var favoriteCategories = await GetMergedFavoriteCategoryMenuItemsAsync(mailCategoryService, holdingAccounts, allAccountFolders, mergedAccountFolderMenuItem.Parameter).ConfigureAwait(false);
        preparedFolderMenuItems.AddRange(favoriteCategories);

        return preparedFolderMenuItems;
    }

    private static async Task<IEnumerable<IMenuItem>> GetFavoriteCategoryMenuItemsAsync(IMailCategoryService mailCategoryService,
                                                                                        MailAccount account,
                                                                                        IEnumerable<IMailItemFolder> handlingFolders,
                                                                                        IMenuItem parentMenuItem)
    {
        var favoriteCategories = await mailCategoryService.GetFavoriteCategoriesAsync(account.Id).ConfigureAwait(false);

        if (!favoriteCategories.Any())
            return [];

        var availableFolders = handlingFolders
            .Where(a => a.IsMoveTarget)
            .Cast<IMailItemFolder>()
            .ToList();

        return favoriteCategories
            .Select(category => (IMenuItem)new MailCategoryMenuItem(category, account, availableFolders, parentMenuItem))
            .ToList();
    }

    private static async Task<IEnumerable<IMenuItem>> GetMergedFavoriteCategoryMenuItemsAsync(IMailCategoryService mailCategoryService,
                                                                                              IEnumerable<MailAccount> holdingAccounts,
                                                                                              IEnumerable<IEnumerable<MailItemFolder>> allAccountFolders,
                                                                                              MergedInbox mergedInbox)
    {
        var categoriesByAccount = new List<(MailAccount Account, List<MailCategory> Categories)>();

        foreach (var account in holdingAccounts)
        {
            var categories = await mailCategoryService.GetFavoriteCategoriesAsync(account.Id).ConfigureAwait(false);
            if (categories.Any())
            {
                categoriesByAccount.Add((account, categories));
            }
        }

        if (!categoriesByAccount.Any())
            return [];

        var handlingFolders = allAccountFolders
            .SelectMany(a => a)
            .Where(a => a.IsMoveTarget)
            .Cast<IMailItemFolder>()
            .ToList();

        return categoriesByAccount
            .SelectMany(a => a.Categories)
            .GroupBy(a => NormalizeCategoryName(a.Name), StringComparer.OrdinalIgnoreCase)
            .Select(group => (IMenuItem)new MergedMailCategoryMenuItem(group.ToList(), handlingFolders, mergedInbox))
            .OrderBy(item => ((MergedMailCategoryMenuItem)item).FolderName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string NormalizeCategoryName(string name)
        => name?.Trim() ?? string.Empty;

    private static HashSet<SpecialFolderType> FindCommonFolders(List<List<MailItemFolder>> lists)
    {
        var allSpecialTypesExceptOther = Enum.GetValues<SpecialFolderType>().Cast<SpecialFolderType>().Where(a => a != SpecialFolderType.Other).ToList();

        // Start with all special folder types from the first list
        var commonSpecialFolderTypes = new HashSet<SpecialFolderType>(allSpecialTypesExceptOther);

        // Intersect with special folder types from all lists
        foreach (var list in lists)
        {
            commonSpecialFolderTypes.IntersectWith(list.Select(f => f.SpecialFolderType));
        }

        return commonSpecialFolderTypes;
    }
}
