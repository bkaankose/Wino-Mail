using System;
using System.IO;
using MimeKit;

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
