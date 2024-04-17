using System;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Messages.Accounts
{
    /// <summary>
    /// When menu item for the account is requested to be extended.
    /// Additional properties are also supported to navigate to correct IMailItem.
    /// </summary>
    /// <param name="AutoSelectAccount">Account to extend menu item for.</param>
    /// <param name="FolderId">Folder to select after expansion.</param>
    /// <param name="NavigateMailItem">Mail item to select if possible in the expanded folder.</param>
    public record AccountMenuItemExtended(Guid FolderId, IMailItem NavigateMailItem);
}
