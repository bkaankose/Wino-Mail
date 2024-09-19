using Wino.Core.Domain.Entities.Shared;

namespace Wino.Messaging.UI
{
    public record AccountRemovedMessage(MailAccount Account) : UIMessageBase<AccountRemovedMessage>;
}
