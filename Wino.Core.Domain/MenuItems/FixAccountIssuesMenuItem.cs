using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Core.Domain.MenuItems
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
