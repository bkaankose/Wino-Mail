using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Folders;

namespace MailCal.Core.Domain.MenuItems;

public class FixAccountIssuesMenuItem : MenuItemBase<IMailItemFolder, FolderMenuItem>
{
    public MailAccount Account { get; }

    public FixAccountIssuesMenuItem(MailAccount account, IMenuItem parentAccountMenuItem) : base(null, null, parentAccountMenuItem)
    {
        Account = account;
    }
}
