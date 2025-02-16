using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces
{
    public interface ISpecialImapProviderConfigResolver
    {
        CustomServerInformation GetServerInformation(MailAccount account, AccountCreationDialogResult dialogResult);
    }
}
