using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Intelligence;

[Table("LocalDailyBriefingIgnore")]
public sealed class LocalDailyBriefingIgnoreRow
{
    [PrimaryKey]
    public string Key { get; set; } = string.Empty;

    [Indexed]
    public Guid LocalAccountId { get; set; }

    [Indexed]
    public Guid BriefingId { get; set; }

    public long IgnoredArtifactRevision { get; set; }
    public DateTime IgnoredAtUtc { get; set; }
}
