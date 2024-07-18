using Wino.Core.Domain.Entities;

namespace Wino.Messaging.Server
{
    public record MailUpdatedMessage(MailCopy UpdatedMail) : ServerMessageBase<MailUpdatedMessage>;
}
