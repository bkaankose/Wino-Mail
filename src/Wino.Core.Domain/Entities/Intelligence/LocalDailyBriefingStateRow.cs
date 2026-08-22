using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Intelligence;

[Table("LocalDailyBriefingState")]
public sealed class LocalDailyBriefingStateRow
{
    [PrimaryKey]
    public Guid LocalAccountId { get; set; }

    public DateTime? LastOpenedAtUtc { get; set; }
    public DateTime? LastViewedAtUtc { get; set; }
    public long LastViewedFactRevision { get; set; }
}
