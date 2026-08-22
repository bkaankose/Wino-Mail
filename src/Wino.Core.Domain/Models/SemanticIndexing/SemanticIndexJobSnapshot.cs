using System;

namespace Wino.Core.Domain.Models.SemanticIndexing;

public enum SemanticIndexJobStatus
{
    Idle,
    Calculating,
    Queued,
    Indexing,
    GeneratingInsights,
    TranslatingHeadlines,
    PausedForQuota,
    Completed,
    Failed,
    Cancelled,
}

public sealed record SemanticIndexJobSnapshot(
    Guid LocalAccountId,
    SemanticIndexJobStatus Status,
    int UploadedMessageCount,
    int SelectedMessageCount,
    string? ErrorCode = null,
    int FailedMessageCount = 0,
    int RestoredMessageCount = 0)
{
    public bool IsActive => Status is
        SemanticIndexJobStatus.Calculating or
        SemanticIndexJobStatus.Queued or
        SemanticIndexJobStatus.Indexing or
        SemanticIndexJobStatus.GeneratingInsights or
        SemanticIndexJobStatus.TranslatingHeadlines;

    public int ProcessedMessageCount
        => UploadedMessageCount + RestoredMessageCount + FailedMessageCount;

    public int SucceededMessageCount
        => UploadedMessageCount + RestoredMessageCount;
}

public enum SemanticMessageIndexState
{
    NotIndexed,
    Queued,
    Indexing,
    Indexed,
    Failed,
    Unsupported,
}
