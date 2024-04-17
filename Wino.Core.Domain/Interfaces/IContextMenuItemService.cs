using System.Collections.Generic;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Menus;

namespace Wino.Core.Domain.Interfaces
{
    public interface IContextMenuItemService
    {
        IEnumerable<FolderOperationMenuItem> GetFolderContextMenuActions(IBaseFolderMenuItem folderInformation);
        IEnumerable<MailOperationMenuItem> GetMailItemContextMenuActions(IEnumerable<IMailItem> selectedMailItems);
        IEnumerable<MailOperationMenuItem> GetMailItemRenderMenuActions(IMailItem mailItem, bool isDarkEditor);
    }
}
