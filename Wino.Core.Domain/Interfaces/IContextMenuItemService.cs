using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Menus;

namespace Wino.Core.Domain.Interfaces;

public interface IContextMenuItemService
{
    IEnumerable<FolderOperationMenuItem> GetFolderContextMenuActions(IBaseFolderMenuItem folderInformation);
    IEnumerable<MailOperationMenuItem> GetMailItemContextMenuActions(IEnumerable<MailCopy> selectedMailItems);
    IEnumerable<MailOperationMenuItem> GetMailItemRenderMenuActions(MailCopy mailItem, bool isDarkEditor);
}
