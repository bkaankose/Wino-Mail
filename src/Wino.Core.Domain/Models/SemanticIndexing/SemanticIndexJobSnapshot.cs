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

/// <summary>
/// What the management screen knows about one account, read from local state.
/// There is no server-side index to describe any more, so every number here comes from
/// the device's own intelligence database.
/// </summary>
public sealed record MailIntelligenceAccountState(
    bool IsEnabled,
    Guid? MailboxId,
    int ProcessedMessageCount,
    int WaitingMessageCount,
    bool HasEligibleMessages,
    int ActiveJobCount)
{
    public bool IsUpToDate => WaitingMessageCount == 0 && ActiveJobCount == 0;

    public bool HasIntelligenceData => ProcessedMessageCount > 0;

    public static MailIntelligenceAccountState Empty { get; } = new(false, null, 0, 0, false, 0);
}
