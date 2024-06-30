using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MoreLinq.Extensions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.MenuItems
{
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
            foreach (var item in this)
            {
                if (item is MergedAccountMenuItem mergedAccountMenuItem)
                {
                    foreach (var singleItem in mergedAccountMenuItem.SubMenuItems.OfType<IAccountMenuItem>())
                    {
                        yield return singleItem;
                    }

                    yield return mergedAccountMenuItem;
                }
                else if (item is IAccountMenuItem accountMenuItem)
                    yield return accountMenuItem;
            }
        }

        public bool TryGetAccountMenuItem(Guid accountId, out IAccountMenuItem value)
        {
            value = this.OfType<AccountMenuItem>().FirstOrDefault(a => a.AccountId == accountId);
            value ??= this.OfType<MergedAccountMenuItem>().FirstOrDefault(a => a.SubMenuItems.OfType<AccountMenuItem>().Where(b => b.AccountId == accountId) != null);

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
            value = this.OfType<MergedAccountFolderMenuItem>()
                    .Where(a => a.MergedInbox.Id == mergedInboxId)
                    .FirstOrDefault(a => a.SpecialFolderType == specialFolderType);

            return value != null;
        }

        public bool TryGetFolderMenuItem(Guid folderId, out IBaseFolderMenuItem value)
        {
            // Root folders
            value = this.OfType<IBaseFolderMenuItem>()
                    .FirstOrDefault(a => a.HandlingFolders.Any(b => b.Id == folderId));

            value ??= this.OfType<FolderMenuItem>()
                .SelectMany(a => a.SubMenuItems)
                    .OfType<IBaseFolderMenuItem>()
                    .FirstOrDefault(a => a.HandlingFolders.Any(b => b.Id == folderId));

            return value != null;
        }

        public bool TryGetSpecialFolderMenuItem(Guid accountId, SpecialFolderType specialFolderType, out FolderMenuItem value)
        {
            value = this.OfType<IBaseFolderMenuItem>()
                    .FirstOrDefault(a => a.HandlingFolders.Any(b => b.MailAccountId == accountId && b.SpecialFolderType == specialFolderType)) as FolderMenuItem;

            return value != null;
        }

        public void RemoveFolderMenuItem(IBaseFolderMenuItem baseFolderMenuItem)
        {
            if (baseFolderMenuItem is FolderMenuItem rootFolderItem)
            {
                if (rootFolderItem.ParentMenuItem == null)
                    Remove(rootFolderItem);
                else if (rootFolderItem.ParentMenuItem is FolderMenuItem parentFolderMenuItem)
                    parentFolderMenuItem.SubMenuItems.Remove(rootFolderItem);
                else if (rootFolderItem.ParentMenuItem is AccountMenuItem parentAccountMenuItem)
                {
                    bool removed = parentAccountMenuItem.SubMenuItems.Remove(rootFolderItem);

                    // Might be in More menu item.
                    if (!removed)
                    {

                    }
                }

                else if (rootFolderItem.ParentMenuItem is MergedAccountMenuItem parentMergedAccountMenuItem)
                    parentMergedAccountMenuItem.SubMenuItems.Remove(rootFolderItem);
            }
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

        public async Task ReplaceFoldersAsync(IEnumerable<IMenuItem> folders)
        {
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                ClearFolderAreaMenuItems();

                Items.Add(new SeperatorItem());
                AddRange(folders);
            });
        }

        /// <summary>
        /// Enables/disables account menu items in the list.
        /// </summary>
        /// <param name="isEnabled">Whether menu items should be enabled or disabled.</param>
        public async Task SetAccountMenuItemEnabledStatusAsync(bool isEnabled)
        {
            var accountItems = this.Where(a => a is IAccountMenuItem).Cast<IAccountMenuItem>();

            await _dispatcher.ExecuteOnUIThread(() =>
            {
                accountItems.ForEach(a => a.IsEnabled = isEnabled);
            });
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
            var itemsToRemove = this.Where(a => !_preservingTypesForFolderArea.Contains(a.GetType())).ToList();

            itemsToRemove.ForEach(item =>
            {
                item.IsExpanded = false;
                item.IsSelected = false;
            });

            RemoveRange(itemsToRemove);
        }
    }
}
