using System.Collections.Generic;
using Wino.Domain.Models.Folders;
using Wino.Domain.Models.MailItem;
using Wino.Domain.Models.Menus;

namespace Wino.Domain.Interfaces
{
    public interface IContextMenuItemService
    {
        IEnumerable<FolderOperationMenuItem> GetFolderContextMenuActions(IBaseFolderMenuItem folderInformation);
        IEnumerable<MailOperationMenuItem> GetMailItemContextMenuActions(IEnumerable<IMailItem> selectedMailItems);
        IEnumerable<MailOperationMenuItem> GetMailItemRenderMenuActions(IMailItem mailItem, bool isDarkEditor);
    }
}
