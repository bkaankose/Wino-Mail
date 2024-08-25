using Wino.Core.Domain.Entities;

namespace Wino.Messaging.UI
{
    public record DraftFailed(MailCopy DraftMail, MailAccount Account) : UIMessageBase<DraftFailed>;
}
