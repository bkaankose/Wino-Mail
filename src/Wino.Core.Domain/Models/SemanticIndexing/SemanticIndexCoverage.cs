using System;

namespace Wino.Core.Domain.Models.SemanticIndexing;

public enum SemanticIndexCoverageMode
{
    DateRange,
    LatestCount,
}
public sealed record SemanticIndexFolderCoverageRule(
    string RemoteFolderId,
    SemanticIndexCoverageMode Mode,
    SemanticIndexRangePreset DatePreset,
    DateTimeOffset? CutoffUtc,
    DateTimeOffset? ThroughUtcExclusive,
    int LatestMessageCount)
{
    public static SemanticIndexFolderCoverageRule Latest(string remoteFolderId, int count = 100)
        => new(remoteFolderId, SemanticIndexCoverageMode.LatestCount, SemanticIndexRangePreset.OneMonth, null, null, Math.Max(0, count));

    public static SemanticIndexFolderCoverageRule DateRange(
        string remoteFolderId,
        SemanticIndexRangePreset preset,
        DateTimeOffset? cutoffUtc,
        DateTimeOffset? throughUtcExclusive)
        => new(remoteFolderId, SemanticIndexCoverageMode.DateRange, preset, cutoffUtc, throughUtcExclusive, 0);
}
