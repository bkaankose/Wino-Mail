using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.MenuItems;

namespace Wino.Core.Extensions
{
    public static class FolderTreeExtensions
    {
        private static MenuItemBase<IMailItemFolder, FolderMenuItem> GetMenuItemByFolderRecursive(IMailItemFolder structure, AccountMenuItem parentAccountMenuItem, IMenuItem parentFolderItem)
        {
            MenuItemBase<IMailItemFolder, FolderMenuItem> parentMenuItem = new FolderMenuItem(structure, parentAccountMenuItem.Parameter, parentFolderItem);

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
