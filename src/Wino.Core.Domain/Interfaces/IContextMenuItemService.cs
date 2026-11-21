using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Menus;

namespace Wino.Core.Domain.Interfaces;

public interface IContextMenuItemService
{
    /// <param name="canRenameAndDelete">False for a folder another user shares without a known RFC 4314 x right.</param>
    /// <param name="canCreateSubFolder">False for a folder another user shares without a known RFC 4314 k right.</param>
    IEnumerable<FolderOperationMenuItem> GetFolderContextMenuActions(bool isSticky, bool isSystemFolder, bool canRenameAndDelete = true, bool canCreateSubFolder = true);
    IEnumerable<MailOperationMenuItem> GetMailItemContextMenuActions(IEnumerable<MailCopy> selectedMailItems);
    IEnumerable<MailOperationMenuItem> GetMailItemRenderMenuActions(MailCopy mailItem, bool isDarkEditor);
    IReadOnlyList<CalendarContextMenuItem> GetCalendarItemContextMenuItems(CalendarItem calendarItem);
}
