using Wino.Domain.Enums;

namespace Wino.Domain.Models.Accounts
{
    public record AccountCreationDialogResult(MailProviderType ProviderType, string AccountName, string SenderName, string AccountColorHex = "");
}
