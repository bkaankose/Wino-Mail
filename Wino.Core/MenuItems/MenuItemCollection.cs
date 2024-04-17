using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.MenuItems
{
    public class MenuItemCollection : ObservableRangeCollection<IMenuItem>
    {
        public IEnumerable<IBaseFolderMenuItem> GetFolderItems(Guid folderId)
        {
            var rootItems = this.OfType<AccountMenuItem>()
                    .SelectMany(a => a.FlattenedFolderHierarchy)
                    .Where(a => a.Parameter?.Id == folderId)
                    .Cast<IBaseFolderMenuItem>();

            // Accounts that are merged can't exist in the root items.
            // Therefore if the folder is found in root items, return it without searching inside merged accounts.

            if (rootItems.Any()) return rootItems;

            var mergedItems = this.OfType<MergedAccountMenuItem>()
                .SelectMany(a => a.SubMenuItems.OfType<MergedAccountFolderMenuItem>()
                .Where(a => a.Parameter.Any(b => b.Id == folderId)))
                .Cast<IBaseFolderMenuItem>();

            // Folder is found in the MergedInbox shared folders.
            if (mergedItems.Any()) return mergedItems;

            // Folder is not in any of the above. Looks inside the individual accounts in merged inbox account menu item.
            var mergedAccountItems = this.OfType<MergedAccountMenuItem>()
                .SelectMany(a => a.SubMenuItems.OfType<AccountMenuItem>()
                               .SelectMany(a => a.FlattenedFolderHierarchy)
                                              .Where(a => a.Parameter?.Id == folderId))
                .Cast<IBaseFolderMenuItem>();

            return mergedAccountItems;
        }

        public IBaseFolderMenuItem GetFolderItem(Guid folderId) => GetFolderItems(folderId).FirstOrDefault();

        public IAccountMenuItem GetAccountMenuItem(Guid accountId)
        {
            if (accountId == null) return null;

            if (TryGetRootAccountMenuItem(accountId, out IAccountMenuItem rootAccountMenuItem)) return rootAccountMenuItem;

            return null;
        }

        // Pattern: Look for root account menu item only. Don't search inside the merged account menu item.
        public bool TryGetRootAccountMenuItem(Guid accountId, out IAccountMenuItem value)
        {
            value = this.OfType<IAccountMenuItem>().FirstOrDefault(a => a.HoldingAccounts.Any(b => b.Id == accountId));

            value ??= this.OfType<MergedAccountMenuItem>().FirstOrDefault(a => a.EntityId == accountId);

            return value != null;
        }

        // Pattern: Look for root account menu item only and return the folder menu item inside the account menu item that has specific special folder type.
        public bool TryGetRootSpecialFolderMenuItem(Guid accountId, SpecialFolderType specialFolderType, out FolderMenuItem value)
        {
            value = this.OfType<AccountMenuItem>()
                    .Where(a => a.HoldingAccounts.Any(b => b.Id == accountId))
                    .SelectMany(a => a.FlattenedFolderHierarchy)
                    .FirstOrDefault(a => a.Parameter?.SpecialFolderType == specialFolderType);

            return value != null;
        }

        // Pattern: Look for special folder menu item inside the loaded folders for Windows Mail style menu items.
        public bool TryGetWindowsStyleRootSpecialFolderMenuItem(Guid accountId, SpecialFolderType specialFolderType, out FolderMenuItem value)
        {
            value = this.OfType<IBaseFolderMenuItem>()
                    .FirstOrDefault(a => a.HandlingFolders.Any(b => b.MailAccountId == accountId && b.SpecialFolderType == specialFolderType)) as FolderMenuItem;

            return value != null;
        }

        // Pattern: Find the merged account menu item and return the special folder menu item that belongs to the merged account menu item.
        // This will not look for the folders inside individual account menu items inside merged account menu item.
        public bool TryGetMergedAccountSpecialFolderMenuItem(Guid mergedInboxId, SpecialFolderType specialFolderType, out IBaseFolderMenuItem value)
        {
            value = this.OfType<MergedAccountMenuItem>()
                    .Where(a => a.EntityId == mergedInboxId)
                    .SelectMany(a => a.SubMenuItems)
                    .OfType<MergedAccountFolderMenuItem>()
                    .FirstOrDefault(a => a.SpecialFolderType == specialFolderType);

            return value != null;
        }

        // Pattern: Find the child account menu item inside the merged account menu item, locate the special folder menu item inside the child account menu item.
        public bool TryGetMergedAccountFolderMenuItemByAccountId(Guid accountId, SpecialFolderType specialFolderType, out FolderMenuItem value)
        {
            value = this.OfType<MergedAccountMenuItem>()
                    .SelectMany(a => a.SubMenuItems)
                    .OfType<AccountMenuItem>()
                    .FirstOrDefault(a => a.HoldingAccounts.Any(b => b.Id == accountId))
                    ?.FlattenedFolderHierarchy
                    .OfType<FolderMenuItem>()
                    .FirstOrDefault(a => a.Parameter?.SpecialFolderType == specialFolderType);

            return value != null;
        }

        // Pattern: Find the common folder menu item with special folder type inside the merged account menu item for the given AccountId.
        public bool TryGetMergedAccountRootFolderMenuItemByAccountId(Guid accountId, SpecialFolderType specialFolderType, out MergedAccountFolderMenuItem value)
        {
            value = this.OfType<MergedAccountMenuItem>()
                    .Where(a => a.HoldingAccounts.Any(b => b.Id == accountId))
                    .SelectMany(a => a.SubMenuItems)
                    .OfType<MergedAccountFolderMenuItem>()
                    .FirstOrDefault(a => a.SpecialFolderType == specialFolderType);

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

            accountMenuItem = this.OfType<AccountMenuItem>().FirstOrDefault(a => a.HoldingAccounts.Any(b => b.Id == accountId));

            // Look for the items inside the merged accounts if regular menu item is not found.
            accountMenuItem ??= this.OfType<MergedAccountMenuItem>()
                .FirstOrDefault(a => a.HoldingAccounts.Any(b => b.Id == accountId))?.SubMenuItems
                .OfType<AccountMenuItem>()
                .FirstOrDefault();

            return accountMenuItem;
        }

        public void ReplaceFolders(IEnumerable<IMenuItem> folders)
        {
            ClearFolderAreaMenuItems();

            Items.Add(new SeperatorItem());
            AddRange(folders);
        }

        public void AddAccountMenuItem(IAccountMenuItem accountMenuItem)
        {
            var lastAccount = Items.OfType<IAccountMenuItem>().LastOrDefault();

            // Index 0 is always the New Mail button.
            var insertIndex = lastAccount == null ? 1 : Items.IndexOf(lastAccount) + 1;

            Insert(insertIndex, accountMenuItem);
        }

        private void ClearFolderAreaMenuItems()
        {
            var cloneItems = Items.ToList();

            foreach (var item in cloneItems)
            {
                if (item is SeperatorItem || item is IBaseFolderMenuItem || item is MergedAccountMoreFolderMenuItem)
                {
                    item.IsSelected = false;
                    item.IsExpanded = false;

                    Remove(item);
                }
            }
        }
    }
}
