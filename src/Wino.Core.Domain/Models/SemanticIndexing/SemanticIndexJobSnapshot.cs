using System;

namespace Wino.Core.Domain.Models.SemanticIndexing;

public enum MailIntelligenceJobStatus
{
    Idle,
    Calculating,
    Uploading,
    Waiting,
    Downloading,
    PausedForQuota,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// Progress of one stage, as this device sees it. Jev and Luna advance independently, so
/// the UI reports them separately rather than as one blended percentage.
/// </summary>
public sealed record MailIntelligenceStageProgress(
    string Status,
    int PageCount,
    bool IsImported,
    bool IsAcknowledged)
{
    public static MailIntelligenceStageProgress Pending { get; } = new("pending", 0, false, false);

    public bool IsPublished => string.Equals(Status, "published", StringComparison.Ordinal);
}

/// <summary>
/// What the management screen shows for one account. Several jobs can be in flight for a
/// mailbox at once, so this aggregates them.
/// </summary>
public sealed record MailIntelligenceJobSnapshot(
    Guid LocalAccountId,
    MailIntelligenceJobStatus Status,
    int SelectedMessageCount,
    int ProcessedMessageCount,
    int FailedMessageCount,
    int ActiveJobCount,
    MailIntelligenceStageProgress Jev,
    MailIntelligenceStageProgress Luna,
    string? ErrorCode = null)
{
    public static MailIntelligenceJobSnapshot Idle(Guid localAccountId) => new(
        localAccountId,
        MailIntelligenceJobStatus.Idle,
        0,
        0,
        0,
        0,
        MailIntelligenceStageProgress.Pending,
        MailIntelligenceStageProgress.Pending);

    public bool IsActive => Status is
        MailIntelligenceJobStatus.Calculating or
        MailIntelligenceJobStatus.Uploading or
        MailIntelligenceJobStatus.Waiting or
        MailIntelligenceJobStatus.Downloading;
}

public enum MailMessageIntelligenceState
{
    NotProcessed,
    Queued,
    Processing,
    Processed,
    Failed,
    Unsupported,
}
