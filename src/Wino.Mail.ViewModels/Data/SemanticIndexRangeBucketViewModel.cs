#nullable enable
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One column of the message volume histogram that sits above the range selector.
/// Buckets are created once per available range and only change coverage state
/// while the user drags the selector, so dragging never rebuilds the collection.
/// </summary>
public partial class SemanticIndexRangeBucketViewModel : ObservableObject
{
    /// <summary>
    /// Design height of the histogram. Bars are measured in pixels because the
    /// bars live in an ItemsRepeater that has no shared measuring pass to scale them.
    /// </summary>
    public const double MaximumBarHeight = 64;

    /// <summary>
    /// Design width of the whole histogram, matched by the range selector below it.
    /// </summary>
    public const double HistogramWidth = 560;

    private const double MinimumBarHeight = 2;

    public required int StartOffset { get; init; }
    public required int EndOffset { get; init; }
    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }
    public required int MessageCount { get; init; }
    public required int LocalAndCloudCount { get; init; }
    public required int LocalOnlyCount { get; init; }
    public required int CloudOnlyCount { get; init; }

    /// <summary>
    /// Bar height relative to the busiest bucket of the same range.
    /// </summary>
    public required double BarHeight { get; init; }

    /// <summary>
    /// Column width. Every bucket carries the same width so that the histogram always
    /// spans exactly the width of the range selector below it, whatever the bucket count.
    /// </summary>
    public required double BarWidth { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionOpacity))]
    public partial bool IsSelected { get; set; }

    public double SelectionOpacity => IsSelected ? 1d : 0.38d;
    public bool IsEmpty => LocalAndCloudCount == 0 && LocalOnlyCount == 0 && CloudOnlyCount == 0;
    public double LocalAndCloudHeight => SegmentHeight(LocalAndCloudCount);
    public double LocalOnlyHeight => SegmentHeight(LocalOnlyCount);
    public double CloudOnlyHeight => SegmentHeight(CloudOnlyCount);
    public double EmptyHeight => IsEmpty ? BarHeight : 0;

    public string Tooltip => string.Format(
        Translator.SemanticIndex_CoverageBucketTooltip,
        StartDate.ToString("d MMMM yyyy"),
        EndDate.ToString("d MMMM yyyy"),
        LocalAndCloudCount,
        LocalOnlyCount,
        CloudOnlyCount);

    private double SegmentHeight(int count)
    {
        var total = LocalAndCloudCount + LocalOnlyCount + CloudOnlyCount;
        return total == 0 ? 0 : BarHeight * count / total;
    }

    public static double CalculateBarHeight(int messageCount, int busiestBucketCount)
        => busiestBucketCount <= 0
            ? MinimumBarHeight
            : Math.Max(MinimumBarHeight, messageCount / (double)busiestBucketCount * MaximumBarHeight);
}
