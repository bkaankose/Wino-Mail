using System.Collections.Generic;
using Wino.Domain.Models.Folders;
using Wino.Domain.Models.MailItem;
using Wino.Domain.Models.Menus;

namespace Wino.Domain.Interfaces
{
    public interface IContextMenuProvider
    {
        /// <summary>
        /// Calculates and returns available folder operations for the given folder.
        /// </summary>
        /// <param name="folderInformation">Folder to get actions for.</param>
        IEnumerable<FolderOperationMenuItem> GetFolderContextMenuActions(IMailItemFolder folderInformation);

        /// <summary>
        /// Calculates and returns available context menu items for selected mail items.
        /// </summary>
        /// <param name="folderInformation">Current folder that asks for the menu items.</param>
        /// <param name="selectedMailItems">Selected menu items in the given folder.</param>
        IEnumerable<MailOperationMenuItem> GetMailItemContextMenuActions(IMailItemFolder folderInformation, IEnumerable<IMailItem> selectedMailItems);

        /// <summary>
        /// Calculates and returns available mail operations for mail rendering CommandBar.
        /// </summary>
        /// <param name="mailItem">Rendered mail item.</param>
        /// <param name="activeFolder">Folder that mail item belongs to.</param>
        IEnumerable<MailOperationMenuItem> GetMailItemRenderMenuActions(IMailItem mailItem, IMailItemFolder activeFolder, bool isDarkEditor);
    }
}
