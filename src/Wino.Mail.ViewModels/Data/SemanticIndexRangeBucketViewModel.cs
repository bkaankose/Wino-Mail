#nullable enable
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One column of the message-volume histogram that sits above the coverage range slider.
/// </summary>
/// <remarks>
/// Buckets are rebuilt whenever the folder selection changes, but a rule change only reassigns the
/// three segment counts within the existing columns, so dragging the slider never rebuilds the
/// collection.
/// <para>
/// The three counts partition the bucket. A message that already has a local artifact stays in
/// <see cref="IndexedCount"/> whether or not the current rule selects it, because narrowing the
/// rule does not un-index it — so dragging only ever moves messages between
/// <see cref="SelectedNotIndexedCount"/> and <see cref="OutsideCount"/>.
/// </para>
/// </remarks>
public partial class SemanticIndexRangeBucketViewModel : ObservableObject
{
    /// <summary>
    /// Design height of the histogram. Bars are measured in pixels because they live in an
    /// ItemsRepeater, which has no shared measuring pass to scale them against each other.
    /// </summary>
    public const double MaximumBarHeight = 88;

    private const double MinimumBarHeight = 3;

    public required int StartOffset { get; init; }
    public required int EndOffset { get; init; }
    public required DateOnly NewestDate { get; init; }
    public required DateOnly OldestDate { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageCount))]
    [NotifyPropertyChangedFor(nameof(IndexedHeight))]
    [NotifyPropertyChangedFor(nameof(SelectedNotIndexedHeight))]
    [NotifyPropertyChangedFor(nameof(OutsideHeight))]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    public partial int IndexedCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageCount))]
    [NotifyPropertyChangedFor(nameof(IndexedHeight))]
    [NotifyPropertyChangedFor(nameof(SelectedNotIndexedHeight))]
    [NotifyPropertyChangedFor(nameof(OutsideHeight))]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    public partial int SelectedNotIndexedCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageCount))]
    [NotifyPropertyChangedFor(nameof(IndexedHeight))]
    [NotifyPropertyChangedFor(nameof(SelectedNotIndexedHeight))]
    [NotifyPropertyChangedFor(nameof(OutsideHeight))]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    public partial int OutsideCount { get; set; }

    /// <summary>Bar height relative to the busiest bucket of the same histogram.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndexedHeight))]
    [NotifyPropertyChangedFor(nameof(SelectedNotIndexedHeight))]
    [NotifyPropertyChangedFor(nameof(OutsideHeight))]
    public partial double BarHeight { get; set; } = MinimumBarHeight;

    /// <summary>
    /// Column width, pushed in by the page once it knows how wide the histogram actually is.
    /// Every bucket carries the same width so the histogram spans exactly the width of the slider
    /// below it, whatever the bucket count.
    /// </summary>
    [ObservableProperty]
    public partial double BarWidth { get; set; } = 8;

    public int MessageCount => IndexedCount + SelectedNotIndexedCount + OutsideCount;

    public bool IsEmpty => MessageCount == 0;

    public double IndexedHeight => SegmentHeight(IndexedCount);
    public double SelectedNotIndexedHeight => SegmentHeight(SelectedNotIndexedCount);
    public double OutsideHeight => SegmentHeight(OutsideCount);

    public string Tooltip => string.Format(
        Translator.SemanticIndex_CoverageBucketTooltip,
        OldestDate.ToString("d MMMM yyyy"),
        NewestDate.ToString("d MMMM yyyy"),
        IndexedCount,
        SelectedNotIndexedCount,
        OutsideCount);

    /// <summary>Copies one calculated bucket's counts onto this column.</summary>
    public void Apply(IntelligenceCoverageBucket bucket, int busiestBucketCount)
    {
        IndexedCount = bucket.IndexedCount;
        SelectedNotIndexedCount = bucket.SelectedNotIndexedCount;
        OutsideCount = bucket.OutsideCount;
        BarHeight = CalculateBarHeight(bucket.MessageCount, busiestBucketCount);
    }

    public static SemanticIndexRangeBucketViewModel Create(
        IntelligenceCoverageBucket bucket, int busiestBucketCount) => new()
        {
            StartOffset = bucket.StartOffset,
            EndOffset = bucket.EndOffset,
            NewestDate = bucket.NewestDate,
            OldestDate = bucket.OldestDate,
            IndexedCount = bucket.IndexedCount,
            SelectedNotIndexedCount = bucket.SelectedNotIndexedCount,
            OutsideCount = bucket.OutsideCount,
            BarHeight = CalculateBarHeight(bucket.MessageCount, busiestBucketCount),
        };

    private double SegmentHeight(int count)
        => MessageCount == 0 ? 0 : BarHeight * count / MessageCount;

    public static double CalculateBarHeight(int messageCount, int busiestBucketCount)
        => busiestBucketCount <= 0
            ? MinimumBarHeight
            : Math.Max(MinimumBarHeight, messageCount / (double)busiestBucketCount * MaximumBarHeight);
}
