#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using Wino.Core.Domain.Models.SemanticIndexing;

namespace Wino.Core.Domain.Models.Intelligence;

/// <summary>
/// Answers every question the coverage editor asks, over an
/// <see cref="IntelligenceCoverageInventory"/> and nothing else.
/// </summary>
/// <remarks>
/// Pure and synchronous by design: this runs on the UI thread on every slider tick, so it may not
/// touch the database, the network, or the clock. "Now" is a parameter — the preset cutoffs depend
/// on it, and reading it internally would make range boundaries untestable.
/// </remarks>
public static class IntelligenceCoverageCalculator
{
    public static IntelligenceFolderInventoryStats GetFolderStats(
        IntelligenceCoverageInventory inventory, string remoteFolderId)
    {
        var indices = inventory.GetFolderIndices(remoteFolderId);
        if (indices.Length == 0)
            return new IntelligenceFolderInventoryStats(remoteFolderId, 0, null, null);

        // Indices are newest-first, so the ends of the list are the two extremes.
        var newest = ToDateOnly(inventory.ReceivedAtUtcTicks[indices[0]]);
        var oldest = ToDateOnly(inventory.ReceivedAtUtcTicks[indices[^1]]);
        return new IntelligenceFolderInventoryStats(remoteFolderId, indices.Length, oldest, newest);
    }

    /// <summary>
    /// How many messages in the folder fall in <c>[cutoffUtc, throughUtcExclusive)</c>.
    /// Two binary searches over the folder's newest-first index list.
    /// </summary>
    public static int CountInDateRange(
        IntelligenceCoverageInventory inventory,
        string remoteFolderId,
        DateTimeOffset? cutoffUtc,
        DateTimeOffset? throughUtcExclusive)
    {
        var (start, end) = GetDateRangeSlice(inventory, inventory.GetFolderIndices(remoteFolderId), cutoffUtc, throughUtcExclusive);
        return end - start;
    }

    /// <summary>
    /// The date the <paramref name="count"/>-th newest message in the folder was received — what a
    /// bare "latest 500" actually reaches back to. Null when the folder has no messages.
    /// </summary>
    public static DateOnly? GetLatestCountReach(
        IntelligenceCoverageInventory inventory, string remoteFolderId, int count)
    {
        var indices = inventory.GetFolderIndices(remoteFolderId);
        var taken = Math.Clamp(count, 0, indices.Length);
        return taken == 0 ? null : ToDateOnly(inventory.ReceivedAtUtcTicks[indices[taken - 1]]);
    }

    /// <summary>
    /// Resolves a whole rule set: per-folder counts plus the deduplicated union of every message
    /// the rules select. A message that lives in two selected folders is counted once in
    /// <see cref="IntelligenceCoverageSelection.DistinctSelectedCount"/>.
    /// </summary>
    public static IntelligenceCoverageSelection Resolve(
        IntelligenceCoverageInventory inventory,
        IReadOnlyCollection<SemanticIndexFolderCoverageRule> rules,
        DateTimeOffset nowUtc)
    {
        var folders = new List<IntelligenceFolderSelectionResult>(rules.Count);
        var selected = new BitArray(inventory.TotalMessageCount);
        var distinct = 0;

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.RemoteFolderId))
                continue;

            var indices = inventory.GetFolderIndices(rule.RemoteFolderId);
            var (start, end) = rule.Mode == SemanticIndexCoverageMode.LatestCount
                ? (0, Math.Clamp(rule.LatestMessageCount, 0, indices.Length))
                : GetDateRangeSlice(inventory, indices, ResolveCutoff(rule, nowUtc), rule.ThroughUtcExclusive);

            for (var position = start; position < end; position++)
            {
                var index = indices[position];
                if (selected[index])
                    continue;
                selected[index] = true;
                distinct++;
            }

            folders.Add(new IntelligenceFolderSelectionResult(
                rule.RemoteFolderId,
                indices.Length,
                end - start,
                start,
                end,
                end > start ? ToDateOnly(inventory.ReceivedAtUtcTicks[indices[end - 1]]) : null));
        }

        return new IntelligenceCoverageSelection(inventory, folders, selected, distinct);
    }

    /// <summary>
    /// How many distinct messages the given folders hold between them. A message filed in two of
    /// them counts once, so this is never the sum of the per-folder counts.
    /// </summary>
    public static int CountDistinct(IntelligenceCoverageInventory inventory, IReadOnlySet<string> remoteFolderIds)
    {
        if (remoteFolderIds.Count == 0 || inventory.TotalMessageCount == 0)
            return 0;

        var seen = new BitArray(inventory.TotalMessageCount);
        var count = 0;
        foreach (var remoteFolderId in remoteFolderIds)
        {
            foreach (var index in inventory.GetFolderIndices(remoteFolderId))
            {
                if (seen[index])
                    continue;
                seen[index] = true;
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// The received ticks of every distinct message in the given folders, newest first. The editor
    /// draws its histogram from this, so one folder and a whole selection use the same shape.
    /// </summary>
    public static long[] GetOrderedTicks(IntelligenceCoverageInventory inventory, IReadOnlySet<string> remoteFolderIds)
    {
        if (remoteFolderIds.Count == 0 || inventory.TotalMessageCount == 0)
            return [];

        var seen = new BitArray(inventory.TotalMessageCount);
        var count = 0;
        foreach (var remoteFolderId in remoteFolderIds)
        {
            foreach (var index in inventory.GetFolderIndices(remoteFolderId))
            {
                if (seen[index])
                    continue;
                seen[index] = true;
                count++;
            }
        }

        // Walking the global array keeps the result in the inventory's newest-first order without
        // a sort, because the inventory is already ordered that way.
        var ticks = new long[count];
        var next = 0;
        for (var index = 0; index < inventory.TotalMessageCount && next < count; index++)
        {
            if (seen[index])
                ticks[next++] = inventory.ReceivedAtUtcTicks[index];
        }
        return ticks;
    }

    /// <summary>Per-UTC-day message counts across the union of the given folders.</summary>
    public static IReadOnlyDictionary<DateOnly, int> BuildDailyCounts(
        IntelligenceCoverageInventory inventory, IReadOnlySet<string> remoteFolderIds)
    {
        var counts = new Dictionary<DateOnly, int>();
        if (remoteFolderIds.Count == 0 || inventory.TotalMessageCount == 0)
            return counts;

        var seen = new BitArray(inventory.TotalMessageCount);
        foreach (var remoteFolderId in remoteFolderIds)
        {
            foreach (var index in inventory.GetFolderIndices(remoteFolderId))
            {
                if (seen[index])
                    continue;
                seen[index] = true;
                var day = ToDateOnly(inventory.ReceivedAtUtcTicks[index]);
                counts[day] = counts.GetValueOrDefault(day) + 1;
            }
        }
        return counts;
    }

    /// <summary>
    /// The effective lower bound of a date rule. Mirrors <see cref="SemanticIndexCoverageResolver"/>:
    /// an explicit cutoff wins, otherwise a non-custom preset derives one from <paramref name="nowUtc"/>.
    /// </summary>
    public static DateTimeOffset? ResolveCutoff(SemanticIndexFolderCoverageRule rule, DateTimeOffset nowUtc)
        => rule.CutoffUtc ?? (rule.DatePreset == SemanticIndexRangePreset.Custom
            ? null
            : rule.DatePreset.CreateCutoff(nowUtc));

    /// <summary>
    /// The slice of a newest-first index list covered by <c>[cutoffUtc, throughUtcExclusive)</c>,
    /// as a half-open range of positions in that list.
    /// </summary>
    private static (int Start, int End) GetDateRangeSlice(
        IntelligenceCoverageInventory inventory,
        int[] indices,
        DateTimeOffset? cutoffUtc,
        DateTimeOffset? throughUtcExclusive)
    {
        if (indices.Length == 0)
            return (0, 0);

        // Newest-first means "older than X" is a suffix, so both bounds are one search each.
        var start = throughUtcExclusive is null ? 0 : CountAtLeast(inventory, indices, throughUtcExclusive.Value.UtcTicks);
        var end = cutoffUtc is null ? indices.Length : CountAtLeast(inventory, indices, cutoffUtc.Value.UtcTicks);
        return end <= start ? (start, start) : (start, end);
    }

    /// <summary>
    /// The number of leading entries whose received tick is greater than or equal to
    /// <paramref name="thresholdTicks"/>, i.e. the first position that falls below it.
    /// </summary>
    private static int CountAtLeast(IntelligenceCoverageInventory inventory, int[] indices, long thresholdTicks)
    {
        var low = 0;
        var high = indices.Length;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (inventory.ReceivedAtUtcTicks[indices[middle]] >= thresholdTicks)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private static DateOnly ToDateOnly(long utcTicks)
        => DateOnly.FromDateTime(new DateTime(utcTicks, DateTimeKind.Utc));
}

public readonly record struct IntelligenceFolderInventoryStats(
    string RemoteFolderId,
    int AvailableMessageCount,
    DateOnly? OldestDate,
    DateOnly? NewestDate);

/// <summary>
/// What one folder's rule selects. <see cref="SliceStart"/> and <see cref="SliceEnd"/> are positions
/// in that folder's index list, so a per-folder missing count needs no extra allocation.
/// </summary>
public readonly record struct IntelligenceFolderSelectionResult(
    string RemoteFolderId,
    int AvailableMessageCount,
    int SelectedMessageCount,
    int SliceStart,
    int SliceEnd,
    DateOnly? ReachDate);

/// <summary>The result of applying a whole rule set to an inventory.</summary>
public sealed class IntelligenceCoverageSelection(
    IntelligenceCoverageInventory inventory,
    IReadOnlyList<IntelligenceFolderSelectionResult> folders,
    BitArray selectedByIndex,
    int distinctSelectedCount)
{
    public IReadOnlyList<IntelligenceFolderSelectionResult> Folders { get; } = folders;

    public BitArray SelectedByIndex { get; } = selectedByIndex;

    /// <summary>Messages selected across every folder, counted once each.</summary>
    public int DistinctSelectedCount { get; } = distinctSelectedCount;

    public string[] ToRemoteMessageIds()
    {
        var ids = new string[DistinctSelectedCount];
        var next = 0;
        for (var index = 0; index < inventory.TotalMessageCount && next < ids.Length; index++)
        {
            if (SelectedByIndex[index])
                ids[next++] = inventory.RemoteMessageIds[index];
        }
        return ids;
    }

    /// <summary>How many selected messages the cloud does not have yet.</summary>
    public int CountMissing(IntelligenceCoverageDelta delta)
    {
        var missing = 0;
        for (var index = 0; index < inventory.TotalMessageCount; index++)
        {
            if (SelectedByIndex[index] && delta.MissingByIndex[index])
                missing++;
        }
        return missing;
    }

    /// <summary>How many of one folder's selected messages the cloud does not have yet.</summary>
    public int CountMissing(IntelligenceCoverageDelta delta, IntelligenceFolderSelectionResult folder)
    {
        var indices = inventory.GetFolderIndices(folder.RemoteFolderId);
        var missing = 0;
        for (var position = folder.SliceStart; position < folder.SliceEnd; position++)
        {
            if (delta.MissingByIndex[indices[position]])
                missing++;
        }
        return missing;
    }
}

/// <summary>
/// Which of an inventory's messages the cloud index is missing, as a bitset parallel to the
/// inventory's indices. Resolved once over the whole account: for any subset S of the account,
/// missing(S) is S intersected with this, so no selection change needs another round-trip.
/// </summary>
public sealed record IntelligenceCoverageDelta(
    Guid LocalAccountId,
    DateTimeOffset InventoryBuiltAtUtc,
    BitArray MissingByIndex,
    DateTimeOffset ResolvedAtUtc)
{
    public bool Matches(IntelligenceCoverageInventory inventory)
        => inventory.LocalAccountId == LocalAccountId &&
           inventory.BuiltAtUtc == InventoryBuiltAtUtc &&
           inventory.TotalMessageCount == MissingByIndex.Length;
}
