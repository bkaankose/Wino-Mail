using Wino.Core.Domain.Entities;

namespace Wino.Messaging.UI
{
    public record AccountCreatedMessage(MailAccount Account) : UIMessageBase<AccountCreatedMessage>;
}
