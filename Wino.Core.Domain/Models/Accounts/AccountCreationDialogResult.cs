using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts
{
    public record AccountCreationDialogResult(MailProviderType ProviderType, string AccountName, SpecialImapProviderDetails SpecialImapProviderDetails);
}
