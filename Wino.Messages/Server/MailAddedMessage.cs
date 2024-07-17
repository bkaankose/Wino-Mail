using Wino.Core.Domain.Entities;

namespace Wino.Messaging.Server
{
    public record MailAddedMessage(MailCopy AddedMail) : ServerMessageBase<MailAddedMessage>;
}
