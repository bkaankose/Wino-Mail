using System;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Core.Domain.Models.SemanticIndexing;

public sealed record SemanticIndexPlan(
    Guid LocalAccountId,
    SemanticIndexRangePreset RangePreset,
    DateTimeOffset? CutoffUtc,
    DateTimeOffset? ThroughUtcExclusive,
    bool AutomaticallyIndexNewMessages,
    int EligibleMessageCount,
    int MissingMessageCount,
    TimeSpan EstimatedDuration,
    bool RequiresReset);

public sealed record SemanticIndexAvailableRange(
    DateOnly OldestDate,
    DateOnly NewestDate,
    IReadOnlyDictionary<DateOnly, int> MessageCountsByDate)
{
    public int TotalMessageCount => MessageCountsByDate.Values.Sum();
    public int DaySpan => Math.Max(0, NewestDate.DayNumber - OldestDate.DayNumber);
}

public enum SemanticIndexJobStatus
{
    Idle,
    Calculating,
    Queued,
    Indexing,
    TranslatingHeadlines,
    PausedForSynchronization,
    PausedForQuota,
    Completed,
    Failed,
    Cancelled,
}

public sealed record SemanticIndexJobSnapshot(
    Guid LocalAccountId,
    SemanticIndexJobStatus Status,
    int CompletedMessageCount,
    int TotalMessageCount,
    string? ErrorCode = null,
    int EmbeddingFailedMessageCount = 0,
    int MetadataCompletedMessageCount = 0,
    int MetadataFailedMessageCount = 0)
{
    public bool IsActive => Status is SemanticIndexJobStatus.Queued or SemanticIndexJobStatus.Indexing or SemanticIndexJobStatus.TranslatingHeadlines or SemanticIndexJobStatus.PausedForSynchronization;
    public int EmbeddingProcessedMessageCount => CompletedMessageCount + EmbeddingFailedMessageCount;
    public int MetadataProcessedMessageCount => MetadataCompletedMessageCount + MetadataFailedMessageCount;
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

public sealed record SemanticIndexJobIntent(
    Guid LocalAccountId,
    Guid ServerMailboxId,
    SemanticIndexRangePreset RangePreset,
    DateTimeOffset? CutoffUtc,
    DateTimeOffset? ThroughUtcExclusive,
    bool AutomaticallyIndexNewMessages,
    string BackfillStatus,
    DateTimeOffset UpdatedAtUtc);
