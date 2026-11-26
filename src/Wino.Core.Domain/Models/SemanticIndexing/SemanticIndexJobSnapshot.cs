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
/// What the management screen shows for one account's current run. A run sends the selection
/// in batches; results stream back per message, so the counts move while it runs. Nothing of a
/// run outlives the process.
/// </summary>
/// <param name="SelectedMessageCount">Messages the run will send.</param>
/// <param name="ProcessedMessageCount">Messages that got a classification so far.</param>
/// <param name="FailedMessageCount">Failure items received so far, either stage.</param>
/// <param name="ActiveBatchCount">Batches of this run streaming right now.</param>
public sealed record MailIntelligenceJobSnapshot(
    Guid LocalAccountId,
    MailIntelligenceJobStatus Status,
    int SelectedMessageCount,
    int ProcessedMessageCount,
    int FailedMessageCount,
    int ActiveBatchCount,
    string? ErrorCode = null)
{
    public static MailIntelligenceJobSnapshot Idle(Guid localAccountId) => new(
        localAccountId,
        MailIntelligenceJobStatus.Idle,
        0,
        0,
        0,
        0);

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
    int ActiveBatchCount)
{
    public bool IsUpToDate => WaitingMessageCount == 0 && ActiveBatchCount == 0;

    public bool HasIntelligenceData => ProcessedMessageCount > 0;

    public static MailIntelligenceAccountState Empty { get; } = new(false, null, 0, 0, false, 0);
}
