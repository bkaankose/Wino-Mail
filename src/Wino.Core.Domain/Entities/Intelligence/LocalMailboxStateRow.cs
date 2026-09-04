using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Intelligence;

[Table("LocalMailboxState")]
public sealed class LocalMailboxStateRow
{
    [PrimaryKey]
    public Guid LocalAccountId { get; set; }

    public Guid MailboxId { get; set; }
    public string IntelligenceVersion { get; set; } = string.Empty;
    public string IndexEpoch { get; set; } = string.Empty;
    public long LastImportedRevision { get; set; }
    public string HeadlineLanguage { get; set; } = string.Empty;
    public bool SuppressHeadlineLanguagePrompt { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string CoverageRulesJson { get; set; } = string.Empty;
}
