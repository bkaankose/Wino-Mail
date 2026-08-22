using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Intelligence;

[Table("LocalArtifact")]
public sealed class LocalArtifactRow
{
    [PrimaryKey]
    public string Key { get; set; } = string.Empty;

    [Indexed]
    public Guid LocalAccountId { get; set; }

    public Guid MailboxId { get; set; }

    [Indexed]
    public string RemoteMessageId { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    [Indexed]
    public string CapabilityId { get; set; } = string.Empty;

    public int GenerationVersion { get; set; }
    public int PayloadSchemaVersion { get; set; }

    [Indexed]
    public long ArtifactRevision { get; set; }

    public DateTime GeneratedAtUtc { get; set; }
    public double? Confidence { get; set; }
    public bool IsDeleted { get; set; }
    public string PayloadJson { get; set; } = "{}";
}
