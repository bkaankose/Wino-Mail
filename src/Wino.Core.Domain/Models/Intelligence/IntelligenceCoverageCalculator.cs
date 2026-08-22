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
    /// Turns a set of remote message ids into a bitmap over the inventory's index space, so the
    /// histogram can test membership per message without hashing a string per bar.
    /// </summary>
    public static BitArray BuildIndexBitmap(
        IntelligenceCoverageInventory inventory, IReadOnlySet<string> remoteMessageIds)
    {
        var bitmap = new BitArray(inventory.TotalMessageCount);
        if (remoteMessageIds.Count == 0)
            return bitmap;

        // Iterating the smaller side would be tempting, but only the inventory knows the index of
        // an id, and an id it has never heard of has no bit to set.
        foreach (var remoteMessageId in remoteMessageIds)
        {
            if (inventory.TryGetMessageIndex(remoteMessageId, out var index))
                bitmap[index] = true;
        }
        return bitmap;
    }

    /// <summary>
    /// The message-volume histogram behind the coverage editor's range slider: equal-width slices
    /// of the folder union in the inventory's newest-first order, each split into the three states
    /// the editor draws.
    /// </summary>
    /// <remarks>
    /// Slices are cut by message position rather than by calendar day, because that is what the
    /// slider addresses — a "newest 500" rule has to line up with the bars above it, and calendar
    /// buckets would put a quiet month and a busy one on the same footing.
    /// <para>
    /// The three states are mutually exclusive and cover every message: a message that already has
    /// an artifact counts as indexed whether or not the current rule selects it, because narrowing
    /// the rule does not un-index it. So dragging the slider only ever moves messages between
    /// <see cref="IntelligenceCoverageBucket.SelectedNotIndexedCount"/> and
    /// <see cref="IntelligenceCoverageBucket.OutsideCount"/>.
    /// </para>
    /// </remarks>
    /// <param name="selectedByIndex">
    /// <see cref="IntelligenceCoverageSelection.SelectedByIndex"/> for the rules being previewed.
    /// </param>
    /// <param name="indexedByIndex">
    /// Messages holding a live local artifact, from <see cref="BuildIndexBitmap"/>.
    /// </param>
    public static IntelligenceCoverageBuckets BuildBuckets(
        IntelligenceCoverageInventory inventory,
        IReadOnlySet<string> remoteFolderIds,
        BitArray selectedByIndex,
        BitArray indexedByIndex,
        int bucketCount)
    {
        if (bucketCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(bucketCount));

        var ordered = GetOrderedIndices(inventory, remoteFolderIds);
        if (ordered.Length == 0)
            return IntelligenceCoverageBuckets.Empty;

        // Fewer messages than buckets would otherwise produce empty bars at the far end, which read
        // as "no mail here" rather than "the histogram ran out of mail".
        var effectiveCount = Math.Min(bucketCount, ordered.Length);
        var buckets = new IntelligenceCoverageBucket[effectiveCount];
        var totalIndexed = 0;
        var totalSelectedNotIndexed = 0;

        for (var bucket = 0; bucket < effectiveCount; bucket++)
        {
            // Spreading the remainder across the leading buckets keeps every bar within one
            // message of every other, so no bar is visibly short just from integer division.
            var start = (int)((long)ordered.Length * bucket / effectiveCount);
            var end = (int)((long)ordered.Length * (bucket + 1) / effectiveCount);

            var indexed = 0;
            var selectedNotIndexed = 0;
            for (var position = start; position < end; position++)
            {
                var index = ordered[position];
                if (indexedByIndex[index])
                    indexed++;
                else if (selectedByIndex[index])
                    selectedNotIndexed++;
            }

            totalIndexed += indexed;
            totalSelectedNotIndexed += selectedNotIndexed;
            buckets[bucket] = new IntelligenceCoverageBucket(
                start,
                end,
                ToDateOnly(inventory.ReceivedAtUtcTicks[ordered[start]]),
                ToDateOnly(inventory.ReceivedAtUtcTicks[ordered[end - 1]]),
                indexed,
                selectedNotIndexed,
                end - start - indexed - selectedNotIndexed);
        }

        return new IntelligenceCoverageBuckets(
            buckets,
            ordered.Length,
            totalIndexed,
            totalSelectedNotIndexed,
            ordered.Length - totalIndexed - totalSelectedNotIndexed);
    }

    /// <summary>
    /// The inventory indices of every distinct message in the given folders, newest first.
    /// </summary>
    private static int[] GetOrderedIndices(
        IntelligenceCoverageInventory inventory, IReadOnlySet<string> remoteFolderIds)
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
        var ordered = new int[count];
        var next = 0;
        for (var index = 0; index < inventory.TotalMessageCount && next < count; index++)
        {
            if (seen[index])
                ordered[next++] = index;
        }
        return ordered;
    }

    /// <summary>
    /// The effective lower bound of a date rule. An explicit cutoff wins; otherwise a non-custom
    /// preset derives one from <paramref name="nowUtc"/>.
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

/// <summary>
/// One bar of the coverage histogram. The three counts partition the bucket, so they always sum to
/// <see cref="MessageCount"/>.
/// </summary>
/// <param name="StartOffset">First position in the folder union, newest-first, inclusive.</param>
/// <param name="EndOffset">Last position in the folder union, exclusive.</param>
/// <param name="NewestDate">Received date at <paramref name="StartOffset"/>.</param>
/// <param name="OldestDate">Received date at the last position in the bucket.</param>
public readonly record struct IntelligenceCoverageBucket(
    int StartOffset,
    int EndOffset,
    DateOnly NewestDate,
    DateOnly OldestDate,
    int IndexedCount,
    int SelectedNotIndexedCount,
    int OutsideCount)
{
    public int MessageCount => IndexedCount + SelectedNotIndexedCount + OutsideCount;
}

/// <summary>The whole histogram, plus the totals the editor prints under it.</summary>
public sealed record IntelligenceCoverageBuckets(
    IReadOnlyList<IntelligenceCoverageBucket> Buckets,
    int MessageCount,
    int IndexedCount,
    int SelectedNotIndexedCount,
    int OutsideCount)
{
    public static IntelligenceCoverageBuckets Empty { get; } = new([], 0, 0, 0, 0);

    /// <summary>
    /// The tallest bucket, so bars can be scaled against their own histogram rather than against a
    /// figure the caller would otherwise have to recompute.
    /// </summary>
    public int BusiestBucketCount
    {
        get
        {
            var busiest = 0;
            foreach (var bucket in Buckets)
            {
                if (bucket.MessageCount > busiest)
                    busiest = bucket.MessageCount;
            }
            return busiest;
        }
    }
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

}
