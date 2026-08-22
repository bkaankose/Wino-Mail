using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Intelligence;

[Table("LocalAccountIntelligenceSnapshot")]
public sealed class LocalAccountIntelligenceSnapshotRow
{
    [PrimaryKey]
    public Guid WinoAccountId { get; set; }

    public string Payload { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
}
