using Wino.Core.Domain.Entities;

namespace Wino.Messaging.UI
{
    public record AccountUpdatedMessage(MailAccount Account) : UIMessageBase<AccountUpdatedMessage>;
}
