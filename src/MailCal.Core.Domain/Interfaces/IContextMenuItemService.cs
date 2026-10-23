using System.Collections.Generic;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Core.Domain.Models.Folders;
using MailCal.Core.Domain.Models.Menus;

namespace MailCal.Core.Domain.Interfaces;

public interface IContextMenuItemService
{
    IEnumerable<FolderOperationMenuItem> GetFolderContextMenuActions(IBaseFolderMenuItem folderInformation);
    IEnumerable<MailOperationMenuItem> GetMailItemContextMenuActions(IEnumerable<MailCopy> selectedMailItems);
    IEnumerable<MailOperationMenuItem> GetMailItemRenderMenuActions(MailCopy mailItem, bool isDarkEditor);
    IReadOnlyList<CalendarContextMenuItem> GetCalendarItemContextMenuItems(CalendarItem calendarItem);
}
