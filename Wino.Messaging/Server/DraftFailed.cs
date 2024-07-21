using Wino.Domain.Entities;

namespace Wino.Messaging.Server
{
    public record DraftFailed(MailCopy DraftMail, MailAccount Account) : ServerMessageBase<DraftFailed>;
}
