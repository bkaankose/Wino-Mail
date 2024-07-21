using Wino.Domain.Entities;
using Wino.Domain.Interfaces;
using Wino.Domain.Models.Folders;

namespace Wino.Mail.ViewModels.Data.MenuItems
{
    public class FixAccountIssuesMenuItem : MenuItemBase<IMailItemFolder, FolderMenuItem>
    {
        public MailAccount Account { get; }

        public FixAccountIssuesMenuItem(MailAccount account, IMenuItem parentAccountMenuItem) : base(null, null, parentAccountMenuItem)
        {
            Account = account;
        }
    }
}
