using Wino.Core.Domain.Entities;

namespace Wino.Messaging.Server
{
    public record DraftCreated(MailCopy DraftMail, MailAccount Account) : ServerMessageBase<DraftCreated>;
}
