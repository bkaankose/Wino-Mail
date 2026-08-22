using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Intelligence;

[Table("LocalIntelligenceAccess")]
public sealed class LocalIntelligenceAccessRow
{
    [PrimaryKey]
    public Guid LocalAccountId { get; set; }

    public Guid WinoAccountId { get; set; }
    public bool HasAiPack { get; set; }
    public bool HasIntelligenceConsent { get; set; }
    public Guid? MailboxId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
