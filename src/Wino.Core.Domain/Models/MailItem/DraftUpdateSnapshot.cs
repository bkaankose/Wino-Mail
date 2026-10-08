using System;
using System.IO;
using MimeKit;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Models.MailItem;

/// <summary>An owned, immutable serialization of one successful local save.</summary>
public sealed class DraftUpdateSnapshot
{
    private readonly byte[] _mime;

    public DraftUpdateSnapshot(Guid accountId, Guid uniqueId, byte[] mime)
    {
        AccountId = accountId;
        UniqueId = uniqueId;
        _mime = (byte[])mime.Clone();
    }

    public Guid Revision { get; } = Guid.NewGuid();
    public Guid AccountId { get; }
    public Guid UniqueId { get; }
    public MimeMessage OpenMime() => MimeMessage.Load(new MemoryStream(_mime, writable: false));
}

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
