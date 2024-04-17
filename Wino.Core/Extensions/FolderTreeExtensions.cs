using System.Linq;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.MenuItems;

namespace Wino.Core.Extensions
{
    public static class FolderTreeExtensions
    {
        public static AccountMenuItem GetAccountMenuTree(this AccountFolderTree accountTree, IMenuItem parentMenuItem = null)
        {
            var accountMenuItem = new AccountMenuItem(accountTree.Account, parentMenuItem);

            foreach (var structure in accountTree.Folders)
            {
                var tree = GetMenuItemByFolderRecursive(structure, accountMenuItem, null);

                accountMenuItem.SubMenuItems.Add(tree);
            }


            // Create flat folder hierarchy for ease of access.
            accountMenuItem.FlattenedFolderHierarchy = ListExtensions
                .FlattenBy(accountMenuItem.SubMenuItems, a => a.SubMenuItems)
                .Where(a => a is FolderMenuItem)
                .Cast<FolderMenuItem>()
                .ToList();

            return accountMenuItem;
        }

        private static MenuItemBase<IMailItemFolder, FolderMenuItem> GetMenuItemByFolderRecursive(IMailItemFolder structure, AccountMenuItem parentAccountMenuItem, IMenuItem parentFolderItem)
        {
            MenuItemBase<IMailItemFolder, FolderMenuItem> parentMenuItem = new FolderMenuItem(structure, parentAccountMenuItem.Parameter, parentAccountMenuItem);

            var childStructures = structure.ChildFolders;

            foreach (var childFolder in childStructures)
            {
                if (childFolder == null) continue;

                // Folder menu item.
                var subChildrenFolderTree = GetMenuItemByFolderRecursive(childFolder, parentAccountMenuItem, parentMenuItem);

                if (subChildrenFolderTree is FolderMenuItem folderItem)
                {
                    parentMenuItem.SubMenuItems.Add(folderItem);
                }
            }

            return parentMenuItem;
        }
    }
}
