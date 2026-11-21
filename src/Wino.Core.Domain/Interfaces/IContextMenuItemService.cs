using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Menus;

namespace Wino.Core.Domain.Interfaces;

public interface IContextMenuItemService
{
    /// <param name="isOwnFolder">False for a folder another user shares: Rename, Delete and Create sub folder are left out.</param>
    IEnumerable<FolderOperationMenuItem> GetFolderContextMenuActions(bool isSticky, bool isSystemFolder, bool isOwnFolder = true);
    IEnumerable<MailOperationMenuItem> GetMailItemContextMenuActions(IEnumerable<MailCopy> selectedMailItems);
    IEnumerable<MailOperationMenuItem> GetMailItemRenderMenuActions(MailCopy mailItem, bool isDarkEditor);
    IReadOnlyList<CalendarContextMenuItem> GetCalendarItemContextMenuItems(CalendarItem calendarItem);
}
