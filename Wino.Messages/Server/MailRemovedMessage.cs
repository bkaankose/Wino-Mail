using Wino.Core.Domain.Entities;

namespace Wino.Messaging.Server
{
    public record MailRemovedMessage(MailCopy RemovedMail) : ServerMessageBase<MailRemovedMessage>;
}
