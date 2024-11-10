using Wino.Core.Domain.Entities.Shared;

namespace Wino.Messaging.UI
{
    public record AccountCreatedMessage(MailAccount Account) : UIMessageBase<AccountCreatedMessage>;
}
