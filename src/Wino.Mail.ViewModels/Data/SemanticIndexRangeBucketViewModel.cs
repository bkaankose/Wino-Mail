#nullable enable
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

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
    public partial SemanticIndexBucketCoverage Coverage { get; set; } = SemanticIndexBucketCoverage.Outside;

    public string Tooltip => string.Format(
        Translator.SemanticIndex_SelectedRangeSummary,
        StartDate.ToString("d MMMM yyyy"),
        EndDate.ToString("d MMMM yyyy"),
        MessageCount);

    public static double CalculateBarHeight(int messageCount, int busiestBucketCount)
        => busiestBucketCount <= 0
            ? MinimumBarHeight
            : Math.Max(MinimumBarHeight, messageCount / (double)busiestBucketCount * MaximumBarHeight);
}
