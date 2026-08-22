using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Intelligence;

[Table("LocalBriefingHeadline")]
public sealed class LocalBriefingHeadlineRow
{
    [PrimaryKey]
    public string Key { get; set; } = string.Empty;

    [Indexed]
    public Guid LocalAccountId { get; set; }

    public Guid MailboxId { get; set; }

    [Indexed]
    public string RemoteMessageId { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;
    public int GenerationVersion { get; set; }

    [Indexed]
    public Guid BriefingId { get; set; }

    public string Headline { get; set; } = string.Empty;
    public long ArtifactRevision { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
