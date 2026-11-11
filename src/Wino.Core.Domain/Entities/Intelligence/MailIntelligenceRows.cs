using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Intelligence;

/// <summary>
/// The Classification decision for one message.
/// Keyed by account, remote message id and content hash: an artifact whose hash no longer
/// matches the locally desired content is stale and is ignored rather than merged.
/// There is no revision column - results are device-local and immutable.
/// </summary>
[Table("ClassificationArtifact")]
public sealed class ClassificationArtifactRow
{
    /// <summary>"{localAccountId:D}|{remoteMessageId}".</summary>
    [PrimaryKey]
    public string Key { get; set; } = string.Empty;

    [Indexed]
    public Guid LocalAccountId { get; set; }

    [Indexed]
    public string RemoteMessageId { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Comma-separated smart-label names, lowercase.</summary>
    public string Labels { get; set; } = string.Empty;

    public string Priority { get; set; } = "normal";

    /// <summary>Comma-separated smart action kind ids Classification hinted at, for example "oneTimeCode".</summary>
    public string Hints { get; set; } = string.Empty;

    [Indexed]
    public bool IncludeInBriefing { get; set; }

    public DateTime CompletedUtc { get; set; }

    /// <summary>
    /// When this artifact first landed locally. Drives "new since viewed" in the briefing,
    /// replacing the artifact revision the old change feed used.
    /// </summary>
    [Indexed]
    public DateTime FirstImportedUtc { get; set; }

    public static string BuildKey(Guid localAccountId, string remoteMessageId)
        => $"{localAccountId:D}|{remoteMessageId}";
}

/// <summary>The Enrichment output for one message: briefing headline and summary, and smart actions.</summary>
[Table("EnrichmentArtifact")]
public sealed class EnrichmentArtifactRow
{
    [PrimaryKey]
    public string Key { get; set; } = string.Empty;

    [Indexed]
    public Guid LocalAccountId { get; set; }

    [Indexed]
    public string RemoteMessageId { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;
    public string Headline { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;

    /// <summary>The typed smart actions as JSON, polymorphic on "kind". Empty when there are none.</summary>
    public string ActionsJson { get; set; } = string.Empty;

    public DateTime CompletedUtc { get; set; }
    public DateTime FirstImportedUtc { get; set; }

    public static string BuildKey(Guid localAccountId, string remoteMessageId)
        => $"{localAccountId:D}|{remoteMessageId}";
}

/// <summary>
/// A submitted job this device is waiting on. Persisted so polling resumes after a
/// restart and so multiple jobs for one mailbox can coexist.
/// </summary>
[Table("MailIntelligenceJob")]
public sealed class MailIntelligenceJobRow
{
    [PrimaryKey]
    public Guid JobId { get; set; }

    [Indexed]
    public Guid LocalAccountId { get; set; }

    public Guid MailboxId { get; set; }
    public int MessageCount { get; set; }

    public string Status { get; set; } = "pending";

    public string ClassificationStatus { get; set; } = "pending";
    public int ClassificationPageCount { get; set; }
    public string? ClassificationDigest { get; set; }
    public bool IsClassificationImported { get; set; }
    public bool IsClassificationAcknowledged { get; set; }

    public string EnrichmentStatus { get; set; } = "pending";
    public int EnrichmentPageCount { get; set; }
    public string? EnrichmentDigest { get; set; }
    public bool IsEnrichmentImported { get; set; }
    public bool IsEnrichmentAcknowledged { get; set; }

    public int FailedCount { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// The device result key this job's results are encrypted to. Null only for a job
    /// submitted before results were encrypted, whose pages the server still sends as JSON.
    /// </summary>
    public string? ResultKeyId { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public bool IsComplete => IsClassificationAcknowledged && IsEnrichmentAcknowledged;
}

/// <summary>
/// A briefing card the user dismissed. Keyed by message identity plus content hash, so a
/// message whose content changed comes back rather than staying hidden.
/// </summary>
[Table("BriefingIgnore")]
public sealed class BriefingIgnoreRow
{
    /// <summary>"{localAccountId:D}|{remoteMessageId}".</summary>
    [PrimaryKey]
    public string Key { get; set; } = string.Empty;

    [Indexed]
    public Guid LocalAccountId { get; set; }

    public string RemoteMessageId { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public DateTime IgnoredUtc { get; set; }

    public static string BuildKey(Guid localAccountId, string remoteMessageId)
        => $"{localAccountId:D}|{remoteMessageId}";
}

/// <summary>When the briefing was last opened and viewed, per account.</summary>
[Table("BriefingViewState")]
public sealed class BriefingViewStateRow
{
    [PrimaryKey]
    public Guid LocalAccountId { get; set; }

    public DateTime? LastOpenedUtc { get; set; }
    public DateTime? LastViewedUtc { get; set; }
}

/// <summary>
/// Per-account intelligence access and the server mailbox id this device submits against.
/// </summary>
[Table("MailIntelligenceAccess")]
public sealed class MailIntelligenceAccessRow
{
    [PrimaryKey]
    public Guid LocalAccountId { get; set; }

    public Guid WinoAccountId { get; set; }
    public Guid MailboxId { get; set; }
    public bool HasAiPack { get; set; }
    public bool HasIntelligenceConsent { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// Cached account-scoped server metadata (billing, consent, usage) so intelligence
/// surfaces render without waiting for the network.
/// </summary>
[Table("AccountIntelligenceSnapshot")]
public sealed class AccountIntelligenceSnapshotRow
{
    [PrimaryKey]
    public Guid WinoAccountId { get; set; }

    public string Payload { get; set; } = "{}";
    public DateTime UpdatedUtc { get; set; }
}
