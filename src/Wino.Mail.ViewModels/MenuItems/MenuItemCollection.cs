using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Collections;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.MenuItems;

public class MenuItemCollection : ObservableRangeCollection<IMenuItem>
{
    // Which types to remove from the list when folders are changing due to selection of new account.
    // We don't clear the whole list since we want to keep the New Mail button and account menu items.
    private readonly Type[] _preservingTypesForFolderArea = [typeof(AccountMenuItem), typeof(NewMailMenuItem), typeof(MergedAccountMenuItem), typeof(AccountGroupMenuItem)];
    /// <summary>The root items with the entries of every account group in their place.</summary>
    public List<IMenuItem> GetAccountAreaItems()
        => this.SelectMany(static item => item is AccountGroupMenuItem group ? group.SubMenuItems.ToArray() : new[] { item }).ToList();

    public IEnumerable<AccountGroupMenuItem> GetAccountGroupMenuItems() => this.OfType<AccountGroupMenuItem>().ToList();

    public IEnumerable<IAccountMenuItem> GetAllAccountMenuItems()
    {
        var rootItems = GetAccountAreaItems();

        foreach (var item in rootItems)
        {
            if (item is MergedAccountMenuItem mergedAccountMenuItem)
            {
                foreach (var singleItem in mergedAccountMenuItem.SubMenuItems.OfType<IAccountMenuItem>().ToList())
                {
                    yield return singleItem;
                }

                yield return mergedAccountMenuItem;
            }
            else if (item is IAccountMenuItem accountMenuItem)
                yield return accountMenuItem;
        }
    }

    public IEnumerable<IBaseFolderMenuItem> GetAllFolderMenuItems(Guid folderId)
        => EnumerateFolderMenuItems(this.ToList())
            .Where(a => a.HandlingFolders.Any(b => b.Id == folderId))
            .ToList();

    /// <summary>Folder entries at any depth: the virtual More folder puts a nested folder two or more levels below the root.</summary>
    private static IEnumerable<IBaseFolderMenuItem> EnumerateFolderMenuItems(IEnumerable<IMenuItem> items)
    {
        foreach (var item in items.OfType<IBaseFolderMenuItem>())
        {
            if (item is IMailCategoryMenuItem || item is IMergedMailCategoryMenuItem)
                continue;

            yield return item;

            foreach (var subItem in EnumerateFolderMenuItems(item.SubMenuItems.ToList()))
            {
                yield return subItem;
            }
        }
    }

    /// <summary>The top-level account entry (account or combined group) with <paramref name="entityId"/>, wherever its group puts it.</summary>
    public IAccountMenuItem FindAccountAreaItem(Guid entityId)
        => GetAccountAreaItems().OfType<IAccountMenuItem>().FirstOrDefault(item => item.EntityId == entityId);

    public bool TryGetAccountMenuItem(Guid accountId, out IAccountMenuItem value)
    {
        var rootItems = GetAccountAreaItems();

        value = rootItems.OfType<AccountMenuItem>().FirstOrDefault(a => a.AccountId == accountId);
        value ??= rootItems.OfType<MergedAccountMenuItem>().FirstOrDefault(a => a.SubMenuItems.OfType<AccountMenuItem>().Any(b => b.AccountId == accountId));

        return value != null;
    }

    // Pattern: Look for special folder menu item inside the loaded folders for Windows Mail style menu items.
    public bool TryGetWindowsStyleRootSpecialFolderMenuItem(Guid accountId, SpecialFolderType specialFolderType, out FolderMenuItem value)
    {
        var rootItems = this.ToList();

        value = rootItems.OfType<IBaseFolderMenuItem>()
                .FirstOrDefault(a => a.HandlingFolders.Any(b => b.MailAccountId == accountId && b.SpecialFolderType == specialFolderType)) as FolderMenuItem;

        return value != null;
    }

    // Pattern: Find the merged account menu item and return the special folder menu item that belongs to the merged account menu item.
    // This will not look for the folders inside individual account menu items inside merged account menu item.
    public bool TryGetMergedAccountSpecialFolderMenuItem(Guid mergedInboxId, SpecialFolderType specialFolderType, out IBaseFolderMenuItem value)
    {
        var rootItems = this.ToList();

        value = rootItems.OfType<MergedAccountFolderMenuItem>()
                .Where(a => a.MergedInbox.Id == mergedInboxId)
                .FirstOrDefault(a => a.SpecialFolderType == specialFolderType);

        return value != null;
    }

    public bool TryGetFolderMenuItem(Guid folderId, out IBaseFolderMenuItem value)
    {
        value = EnumerateFolderMenuItems(this.ToList())
            .FirstOrDefault(a => a.HandlingFolders.Any(b => b.Id == folderId));

        return value != null;
    }

    public bool TryGetCategoryMenuItem(Guid categoryId, out IBaseFolderMenuItem value)
    {
        var rootItems = this.ToList();

        value = rootItems.OfType<IMailCategoryMenuItem>()
            .FirstOrDefault(a => a.MailCategory.Id == categoryId);

        value ??= rootItems.OfType<IMergedMailCategoryMenuItem>()
            .FirstOrDefault(a => a.Categories.Any(b => b.Id == categoryId)) as IBaseFolderMenuItem;

        return value != null;
    }

    public void UpdateUnreadItemCountsToZero()
    {
        // Handle the root folders.
        foreach (var item in this.OfType<IBaseFolderMenuItem>().ToList())
        {
            RecursivelyResetUnreadItemCount(item);
        }
    }

    private void RecursivelyResetUnreadItemCount(IBaseFolderMenuItem baseFolderMenuItem)
    {
        baseFolderMenuItem.UnreadItemCount = 0;

        if (baseFolderMenuItem.SubMenuItems == null) return;

        foreach (var subMenuItem in baseFolderMenuItem.SubMenuItems.OfType<IBaseFolderMenuItem>().ToList())
        {
            RecursivelyResetUnreadItemCount(subMenuItem);
        }
    }

    public bool TryGetSpecialFolderMenuItem(Guid accountId, SpecialFolderType specialFolderType, out FolderMenuItem value)
    {
        var rootItems = this.ToList();

        value = rootItems.OfType<IBaseFolderMenuItem>()
                .FirstOrDefault(a => a.HandlingFolders.Any(b => b.MailAccountId == accountId && b.SpecialFolderType == specialFolderType)) as FolderMenuItem;

        return value != null;
    }

    /// <summary>
    /// Skips the merged account menu item, but directly returns the Account menu item inside the merged account menu item.
    /// </summary>
    /// <param name="accountId">Account id to look for.</param>
    /// <returns>Direct AccountMenuItem inside the Merged Account menu item if exists.</returns>
    public AccountMenuItem GetSpecificAccountMenuItem(Guid accountId)
    {
        AccountMenuItem accountMenuItem = null;
        var rootItems = GetAccountAreaItems();

        accountMenuItem = rootItems.OfType<AccountMenuItem>().FirstOrDefault(a => a.HoldingAccounts.Any(b => b.Id == accountId));

        // Look for the items inside the merged accounts if regular menu item is not found.
        accountMenuItem ??= rootItems.OfType<MergedAccountMenuItem>()
            .FirstOrDefault(a => a.HoldingAccounts.Any(b => b.Id == accountId))?.SubMenuItems
            .OfType<AccountMenuItem>()
            .FirstOrDefault(a => a.AccountId == accountId);

        return accountMenuItem;
    }

    /// <summary>Must run on the UI thread.</summary>
    /// <param name="keepExpansion">
    /// The folders are a rebuilt list of the folders already shown, for example after a folder was created or renamed.
    /// Folders that were expanded, including the virtual More and Categories folders, stay expanded.
    /// </param>
    public void ReplaceFolders(IEnumerable<IMenuItem> folders, bool keepExpansion = false)
    {
        var newFolders = folders.ToList();

        if (keepExpansion)
        {
            var expandedFolderKeys = new HashSet<FolderExpansionKey>();
            CollectExpandedFolderKeys(this, expandedFolderKeys);
            RestoreFolderExpansion(newFolders, expandedFolderKeys);
        }

        ClearFolderAreaMenuItems();
        Add(new SeperatorItem());
        AddRange(newFolders);
    }

    /// <summary>
    /// The virtual More and Categories folders are recreated with an empty id on every rebuild, so they are matched by
    /// their folder type; real folders keep their id across a rename.
    /// </summary>
    private readonly record struct FolderExpansionKey(Guid FolderId, SpecialFolderType SpecialFolderType);

    private static FolderExpansionKey? GetFolderExpansionKey(IMenuItem item)
    {
        if (item is not FolderMenuItem folderMenuItem || folderMenuItem.Parameter == null)
            return null;

        var folder = folderMenuItem.Parameter;

        return folder.Id == Guid.Empty
            ? new FolderExpansionKey(Guid.Empty, folder.SpecialFolderType)
            : new FolderExpansionKey(folder.Id, SpecialFolderType.Other);
    }

    private static void CollectExpandedFolderKeys(IEnumerable<IMenuItem> items, HashSet<FolderExpansionKey> expandedFolderKeys)
    {
        foreach (var item in items.OfType<FolderMenuItem>())
        {
            if (item.IsExpanded && GetFolderExpansionKey(item) is { } key)
                expandedFolderKeys.Add(key);

            CollectExpandedFolderKeys(item.SubMenuItems, expandedFolderKeys);
        }
    }

    private static void RestoreFolderExpansion(IEnumerable<IMenuItem> items, HashSet<FolderExpansionKey> expandedFolderKeys)
    {
        if (expandedFolderKeys.Count == 0)
            return;

        foreach (var item in items.OfType<FolderMenuItem>())
        {
            if (GetFolderExpansionKey(item) is { } key && expandedFolderKeys.Contains(key))
                item.IsExpanded = true;

            RestoreFolderExpansion(item.SubMenuItems, expandedFolderKeys);
        }
    }

    /// <summary>
    /// Enables/disables account menu items in the list.
    /// </summary>
    /// <param name="isEnabled">Whether menu items should be enabled or disabled.</param>
    /// <remarks>Must run on the UI thread.</remarks>
    public void SetAccountMenuItemEnabledStatus(bool isEnabled)
    {
        // Accounts linked into a merged inbox sit under it, and the clicked one was disabled while it loaded too.
        var accountItems = GetAccountAreaItems()
            .OfType<IAccountMenuItem>()
            .SelectMany(static item => item is MergedAccountMenuItem merged
                ? merged.SubMenuItems.OfType<IAccountMenuItem>().Prepend(item)
                : new IAccountMenuItem[] { item })
            .ToList();

        foreach (var item in accountItems)
        {
            item.IsEnabled = isEnabled;
        }
    }

    public void AddAccountMenuItem(IMenuItem accountMenuItem)
    {
        var lastAccount = Items.LastOrDefault(static item => item is IAccountMenuItem or AccountGroupMenuItem);

        // Indexes 0 and 1 are reserved for New Mail and Daily Briefing.
        var insertIndex = lastAccount == null ? 2 : Items.IndexOf(lastAccount) + 1;

        Insert(insertIndex, accountMenuItem);
    }

    public bool RemoveFolderMenuItem(Guid folderId)
    {
        // Check root-level items.
        var rootItem = this.OfType<IBaseFolderMenuItem>()
            .Where(a => a is not IMailCategoryMenuItem && a is not IMergedMailCategoryMenuItem)
            .FirstOrDefault(a => a.HandlingFolders.Any(b => b.Id == folderId));

        if (rootItem != null)
        {
            Remove(rootItem);
            return true;
        }

        // Check sub-items of root folders.
        foreach (var rootFolder in this.OfType<IBaseFolderMenuItem>().ToList())
        {
            var subItem = rootFolder.SubMenuItems
                .OfType<IBaseFolderMenuItem>()
                .FirstOrDefault(a => a.HandlingFolders.Any(b => b.Id == folderId));

            if (subItem != null)
            {
                rootFolder.SubMenuItems.Remove(subItem);
                return true;
            }
        }

        return false;
    }

    private void ClearFolderAreaMenuItems()
    {
        var itemsToRemove = this.Where(a => !_preservingTypesForFolderArea.Contains(a.GetType())).ToList();

        itemsToRemove.ForEach(item =>
        {
            item.IsExpanded = false;
            item.IsSelected = false;

            try
            {
                Remove(item);
            }
            catch (Exception) { }
        });
    }
}
