using Wino.Core.Domain.Entities;

namespace Wino.Messaging.UI
{
    public record MailUpdatedMessage(MailCopy UpdatedMail) : UIMessageBase<MailUpdatedMessage>;
}
