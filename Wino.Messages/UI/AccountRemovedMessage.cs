using Wino.Core.Domain.Entities;

namespace Wino.Messaging.UI
{
    public record AccountRemovedMessage(MailAccount Account) : UIMessageBase<AccountRemovedMessage>;
}
