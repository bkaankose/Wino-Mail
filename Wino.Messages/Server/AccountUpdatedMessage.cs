using Wino.Core.Domain.Entities;

namespace Wino.Messaging.Server
{
    public record AccountUpdatedMessage(MailAccount Account) : ServerMessageBase<AccountUpdatedMessage>;
}
