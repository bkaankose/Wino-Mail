using FluentAssertions;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class SemanticIndexRangeBucketViewModelTests
{
    [Fact]
    public void StackedBands_UseTruthCountsAndPreserveTotalHeight()
    {
        var bucket = Create(indexed: 4, toIndex: 2, outside: 2, height: 64);

        bucket.IndexedHeight.Should().Be(32);
        bucket.SelectedNotIndexedHeight.Should().Be(16);
        bucket.OutsideHeight.Should().Be(16);
        (bucket.IndexedHeight + bucket.SelectedNotIndexedHeight + bucket.OutsideHeight).Should().Be(64);
    }

    [Fact]
    public void EmptyBucket_ReportsNoSegments()
    {
        var bucket = Create(indexed: 0, toIndex: 0, outside: 0, height: 3);

        bucket.IsEmpty.Should().BeTrue();
        bucket.MessageCount.Should().Be(0);
        bucket.IndexedHeight.Should().Be(0);
        bucket.SelectedNotIndexedHeight.Should().Be(0);
        bucket.OutsideHeight.Should().Be(0);
    }

    /// <summary>
    /// Narrowing the rule must not move a message out of the indexed band: it is still indexed,
    /// so only the other two bands may change while dragging.
    /// </summary>
    [Fact]
    public void Apply_MovesMessagesBetweenTheSelectedAndOutsideBandsOnly()
    {
        var bucket = Create(indexed: 3, toIndex: 5, outside: 2, height: 64);

        bucket.Apply(
            new IntelligenceCoverageBucket(0, 10, Newest, Oldest, IndexedCount: 3, SelectedNotIndexedCount: 1, OutsideCount: 6),
            busiestBucketCount: 10);

        bucket.IndexedCount.Should().Be(3);
        bucket.SelectedNotIndexedCount.Should().Be(1);
        bucket.OutsideCount.Should().Be(6);
        bucket.MessageCount.Should().Be(10);
    }

    [Fact]
    public void CalculateBarHeight_ScalesAgainstTheBusiestBucketAndKeepsAFloor()
    {
        SemanticIndexRangeBucketViewModel.CalculateBarHeight(50, 100)
            .Should().Be(SemanticIndexRangeBucketViewModel.MaximumBarHeight / 2);

        // A bucket with almost nothing in it still has to be visible as a bucket.
        SemanticIndexRangeBucketViewModel.CalculateBarHeight(1, 100_000).Should().BeGreaterThan(0);
        SemanticIndexRangeBucketViewModel.CalculateBarHeight(0, 0).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Create_CopiesEveryCountAndBoundaryOffTheCalculatedBucket()
    {
        var bucket = SemanticIndexRangeBucketViewModel.Create(
            new IntelligenceCoverageBucket(4, 12, Newest, Oldest, 2, 3, 3),
            busiestBucketCount: 8);

        bucket.StartOffset.Should().Be(4);
        bucket.EndOffset.Should().Be(12);
        bucket.NewestDate.Should().Be(Newest);
        bucket.OldestDate.Should().Be(Oldest);
        bucket.MessageCount.Should().Be(8);
        bucket.BarHeight.Should().Be(SemanticIndexRangeBucketViewModel.MaximumBarHeight);
    }

    private static readonly DateOnly Newest = new(2026, 8, 15);
    private static readonly DateOnly Oldest = new(2026, 8, 10);

    private static SemanticIndexRangeBucketViewModel Create(int indexed, int toIndex, int outside, double height)
        => new()
        {
            StartOffset = 0,
            EndOffset = indexed + toIndex + outside,
            NewestDate = Newest,
            OldestDate = Oldest,
            IndexedCount = indexed,
            SelectedNotIndexedCount = toIndex,
            OutsideCount = outside,
            BarHeight = height,
            BarWidth = 8,
        };
}
