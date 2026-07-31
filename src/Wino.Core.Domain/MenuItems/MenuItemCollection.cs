using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain.Collections;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.MenuItems;

public class MenuItemCollection : ObservableRangeCollection<IMenuItem>
{
    // Which types to remove from the list when folders are changing due to selection of new account.
    // We don't clear the whole list since we want to keep the New Mail button and account menu items.
    private readonly Type[] _preservingTypesForFolderArea = [typeof(AccountMenuItem), typeof(NewMailMenuItem), typeof(MergedAccountMenuItem)];
    private readonly IDispatcher _dispatcher;

    public MenuItemCollection(IDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public IEnumerable<IAccountMenuItem> GetAllAccountMenuItems()
    {
        var rootItems = this.ToList();

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
    {
        var rootItems = this.ToList();

        foreach (var item in rootItems)
        {
            if (item is IBaseFolderMenuItem folderMenuItem && item is not IMailCategoryMenuItem && item is not IMergedMailCategoryMenuItem)
            {
                if (folderMenuItem.HandlingFolders.Any(a => a.Id == folderId))
                {
                    yield return folderMenuItem;
                }
                else if (folderMenuItem.SubMenuItems.Any())
                {
                    foreach (var subItem in folderMenuItem.SubMenuItems.OfType<IBaseFolderMenuItem>().ToList())
                    {
                        if (subItem.HandlingFolders.Any(a => a.Id == folderId))
                        {
                            yield return subItem;
                        }
                    }

                }
            }
        }
    }

    public bool TryGetAccountMenuItem(Guid accountId, out IAccountMenuItem value)
    {
        var rootItems = this.ToList();

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
        var rootItems = this.ToList();

        // Root folders
        value = rootItems.OfType<IBaseFolderMenuItem>()
                .Where(a => a is not IMailCategoryMenuItem && a is not IMergedMailCategoryMenuItem)
                .FirstOrDefault(a => a.HandlingFolders.Any(b => b.Id == folderId));

        value ??= rootItems.OfType<FolderMenuItem>()
            .SelectMany(a => a.SubMenuItems)
                .OfType<IBaseFolderMenuItem>()
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
        var rootItems = this.ToList();

        accountMenuItem = rootItems.OfType<AccountMenuItem>().FirstOrDefault(a => a.HoldingAccounts.Any(b => b.Id == accountId));

        // Look for the items inside the merged accounts if regular menu item is not found.
        accountMenuItem ??= rootItems.OfType<MergedAccountMenuItem>()
            .FirstOrDefault(a => a.HoldingAccounts.Any(b => b.Id == accountId))?.SubMenuItems
            .OfType<AccountMenuItem>()
            .FirstOrDefault(a => a.AccountId == accountId);

        return accountMenuItem;
    }

    public async Task ReplaceFoldersAsync(IEnumerable<IMenuItem> folders)
    {
        await _dispatcher.ExecuteOnUIThread(() => ClearFolderAreaMenuItems());
        await _dispatcher.ExecuteOnUIThread(() => Items.Add(new SeperatorItem()));
        await _dispatcher.ExecuteOnUIThread(() => AddRange(folders, System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Enables/disables account menu items in the list.
    /// </summary>
    /// <param name="isEnabled">Whether menu items should be enabled or disabled.</param>
    public async Task SetAccountMenuItemEnabledStatusAsync(bool isEnabled)
    {
        var accountItems = this.Where(a => a is IAccountMenuItem).Cast<IAccountMenuItem>().ToList();

        await _dispatcher.ExecuteOnUIThread(() =>
        {
            foreach (var item in accountItems)
            {
                item.IsEnabled = isEnabled;
            }
        });
    }

    public void AddAccountMenuItem(IAccountMenuItem accountMenuItem)
    {
        var lastAccount = Items.OfType<IAccountMenuItem>().LastOrDefault();

        // Index 0 is always the New Mail button.
        var insertIndex = lastAccount == null ? 1 : Items.IndexOf(lastAccount) + 1;

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
