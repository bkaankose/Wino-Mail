using Wino.Core.Domain.Entities;

namespace Wino.Messaging.Server
{
    public record AccountRemovedMessage(MailAccount Account) : ServerMessageBase<AccountRemovedMessage>;
}
