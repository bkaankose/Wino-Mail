using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Models.Accounts;

namespace MailCal.Core.Domain.Interfaces;

public interface ISpecialImapProviderConfigResolver
{
    CustomServerInformation GetServerInformation(MailAccount account, AccountCreationDialogResult dialogResult);
}
