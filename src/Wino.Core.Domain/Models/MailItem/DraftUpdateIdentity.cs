using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Models.MailItem;

public sealed record DraftUpdateIdentity(string MessageId, string DraftId, string ThreadId,
    uint? ImapUid = null, uint? ImapUidValidity = null)
{
    public static DraftUpdateIdentity From(MailCopy mail) =>
        new(mail.Id, mail.DraftId, mail.ThreadId, mail.ImapUid, mail.ImapUidValidity);

    public void Apply(MailCopy mail)
    {
        mail.Id = MessageId;
        mail.DraftId = DraftId;
        mail.ThreadId = ThreadId;
        if (ImapUid.HasValue) mail.ImapUid = ImapUid.Value;
        if (ImapUidValidity.HasValue) mail.ImapUidValidity = ImapUidValidity.Value;
    }
}
