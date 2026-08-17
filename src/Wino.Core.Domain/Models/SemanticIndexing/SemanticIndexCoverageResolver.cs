using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Core.Domain.Models.SemanticIndexing;

public static class SemanticIndexCoverageResolver
{
    public static SemanticIndexCoverageResolution Resolve(
        IReadOnlyList<IntelligenceMessageCandidate> candidates,
        IReadOnlyCollection<SemanticIndexFolderCoverageRule> rules)
    {
        var folderPlans = new List<SemanticIndexFolderPlan>();
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in rules.Where(rule => !string.IsNullOrWhiteSpace(rule.RemoteFolderId)))
        {
            var available = candidates
                .Where(candidate => candidate.RemoteFolderIds.Contains(rule.RemoteFolderId, StringComparer.Ordinal))
                .ToArray();
            var eligible = rule.Mode == SemanticIndexCoverageMode.LatestCount
                ? available.OrderByDescending(candidate => candidate.ReceivedAt)
                    .ThenBy(candidate => candidate.RemoteMessageId, StringComparer.Ordinal)
                    .Take(Math.Max(0, rule.LatestMessageCount))
                    .ToArray()
                : available.Where(candidate => IsInDateRange(candidate.ReceivedAt, rule)).ToArray();

            var ids = eligible.Select(candidate => candidate.RemoteMessageId).ToHashSet(StringComparer.Ordinal);
            selectedIds.UnionWith(ids);
            folderPlans.Add(new SemanticIndexFolderPlan(rule, available.Length, eligible.Length, 0, ids));
        }

        var resolvedCandidates = candidates
            .Where(candidate => selectedIds.Contains(candidate.RemoteMessageId))
            .OrderByDescending(candidate => candidate.ReceivedAt)
            .ThenBy(candidate => candidate.RemoteMessageId, StringComparer.Ordinal)
            .ToArray();
        return new SemanticIndexCoverageResolution(folderPlans, resolvedCandidates);
    }

    private static bool IsInDateRange(DateTimeOffset receivedAt, SemanticIndexFolderCoverageRule rule)
    {
        var presetCutoff = rule.CutoffUtc;
        if (presetCutoff is null && rule.DatePreset != SemanticIndexRangePreset.Custom)
            presetCutoff = rule.DatePreset.CreateCutoff(DateTimeOffset.UtcNow);
        var utc = receivedAt.Offset == TimeSpan.Zero
            ? receivedAt
            : new DateTimeOffset(DateTime.SpecifyKind(receivedAt.DateTime, DateTimeKind.Utc));
        if (presetCutoff is not null && utc < presetCutoff.Value.ToUniversalTime())
            return false;
        if (rule.ThroughUtcExclusive is not null && utc >= rule.ThroughUtcExclusive.Value.ToUniversalTime())
            return false;
        return true;
    }
}
