using Wino.Core.Domain.Entities;

namespace Wino.Messaging.UI
{
    public record MailAddedMessage(MailCopy AddedMail) : UIMessageBase<MailAddedMessage>;
}
