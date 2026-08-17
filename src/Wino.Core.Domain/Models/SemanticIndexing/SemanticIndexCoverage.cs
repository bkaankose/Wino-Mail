using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Models.Intelligence;

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

public sealed record SemanticIndexFolderPlan(
    SemanticIndexFolderCoverageRule Rule,
    int AvailableMessageCount,
    int EligibleMessageCount,
    int MissingMessageCount,
    IReadOnlySet<string> EligibleRemoteMessageIds);

public sealed record SemanticIndexCoverageResolution(
    IReadOnlyList<SemanticIndexFolderPlan> FolderPlans,
    IReadOnlyList<IntelligenceMessageCandidate> Candidates)
{
    public int EligibleMessageCount => Candidates.Count;
    public int MissingMessageCount { get; init; }
}
