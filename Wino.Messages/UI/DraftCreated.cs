using Wino.Core.Domain.Entities;

namespace Wino.Messaging.UI
{
    public record DraftCreated(MailCopy DraftMail, MailAccount Account) : UIMessageBase<DraftCreated>;
}
