using Wino.Core.Domain.Entities;

namespace Wino.Messaging.UI
{
    public record MailRemovedMessage(MailCopy RemovedMail) : UIMessageBase<MailRemovedMessage>;
}
