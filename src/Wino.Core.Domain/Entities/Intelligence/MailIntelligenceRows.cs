using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Intelligence;

/// <summary>
/// The Jev decision for one message.
/// Keyed by account, remote message id and content hash: an artifact whose hash no longer
/// matches the locally desired content is stale and is ignored rather than merged.
/// There is no revision column - results are device-local and immutable.
/// </summary>
[Table("JevArtifact")]
public sealed class JevArtifactRow
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

/// <summary>The Luna headline and summary for one briefing-included message.</summary>
[Table("LunaArtifact")]
public sealed class LunaArtifactRow
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

    public string JevStatus { get; set; } = "pending";
    public int JevPageCount { get; set; }
    public string? JevDigest { get; set; }
    public bool IsJevImported { get; set; }
    public bool IsJevAcknowledged { get; set; }

    public string LunaStatus { get; set; } = "pending";
    public int LunaPageCount { get; set; }
    public string? LunaDigest { get; set; }
    public bool IsLunaImported { get; set; }
    public bool IsLunaAcknowledged { get; set; }

    public int FailedCount { get; set; }
    public string? LastError { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public bool IsComplete => IsJevAcknowledged && IsLunaAcknowledged;
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
