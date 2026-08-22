using System.Linq;
using FluentAssertions;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.AI.Abstractions;
using Xunit;

namespace Wino.Core.Tests.SemanticIndexing;

public sealed class IntelligenceCoverageCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Inventory_OrdersNewestFirstAndCollapsesFolderMemberships()
    {
        var inventory = Build(
            Row("old", "folder-a", 2026, 8, 1),
            Row("shared", "folder-a", 2026, 8, 12),
            Row("shared", "folder-b", 2026, 8, 12),
            Row("new", "folder-a", 2026, 8, 15));

        inventory.RemoteMessageIds.Should().Equal("new", "shared", "old");
        inventory.TotalMessageCount.Should().Be(3);
        inventory.GetFolderIndices("folder-a").Should().Equal(0, 1, 2);
        inventory.GetFolderIndices("folder-b").Should().Equal(1);
    }

    [Fact]
    public void GetFolderStats_ReportsCountAndBothExtremes()
    {
        var inventory = Build(
            Row("a", "folder", 2026, 8, 1),
            Row("b", "folder", 2026, 8, 10),
            Row("c", "folder", 2026, 8, 15));

        var stats = IntelligenceCoverageCalculator.GetFolderStats(inventory, "folder");

        stats.AvailableMessageCount.Should().Be(3);
        stats.OldestDate.Should().Be(new DateOnly(2026, 8, 1));
        stats.NewestDate.Should().Be(new DateOnly(2026, 8, 15));
    }

    [Fact]
    public void GetFolderStats_ForUnknownFolder_IsEmpty()
    {
        var stats = IntelligenceCoverageCalculator.GetFolderStats(Build(Row("a", "folder", 2026, 8, 1)), "missing");

        stats.AvailableMessageCount.Should().Be(0);
        stats.OldestDate.Should().BeNull();
        stats.NewestDate.Should().BeNull();
    }

    [Fact]
    public void CountInDateRange_UsesInclusiveStartAndExclusiveEnd()
    {
        var inventory = Build(
            Row("before", "folder", 2026, 8, 1),
            Row("onCutoff", "folder", 2026, 8, 2),
            Row("inside", "folder", 2026, 8, 2, hour: 12),
            Row("onThrough", "folder", 2026, 8, 3),
            Row("after", "folder", 2026, 8, 4));

        var count = IntelligenceCoverageCalculator.CountInDateRange(
            inventory,
            "folder",
            new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero));

        // The cutoff instant is included, the through instant is not.
        count.Should().Be(2);
    }

    [Fact]
    public void CountInDateRange_WithoutBounds_CountsEverything()
    {
        var inventory = Build(
            Row("a", "folder", 2026, 8, 1),
            Row("b", "folder", 2026, 8, 2));

        IntelligenceCoverageCalculator.CountInDateRange(inventory, "folder", null, null).Should().Be(2);
    }

    [Fact]
    public void CountInDateRange_WhenNothingQualifies_IsZero()
    {
        var inventory = Build(Row("a", "folder", 2026, 8, 1));

        IntelligenceCoverageCalculator.CountInDateRange(
            inventory, "folder", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), null).Should().Be(0);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "2026-08-15")]
    [InlineData(2, "2026-08-10")]
    [InlineData(3, "2026-08-01")]
    [InlineData(99, "2026-08-01")]
    public void GetLatestCountReach_ReportsTheDateTheNthMessageReachesBackTo(int count, string? expected)
    {
        var inventory = Build(
            Row("a", "folder", 2026, 8, 1),
            Row("b", "folder", 2026, 8, 10),
            Row("c", "folder", 2026, 8, 15));

        var reach = IntelligenceCoverageCalculator.GetLatestCountReach(inventory, "folder", count);

        reach.Should().Be(expected is null ? null : DateOnly.Parse(expected));
    }

    [Fact]
    public void Resolve_LatestCount_TakesTheNewestAndDeduplicatesAcrossFolders()
    {
        var inventory = Build(
            Row("a", "folder-a", 2026, 8, 10),
            Row("b", "folder-a", 2026, 8, 11),
            Row("shared", "folder-a", 2026, 8, 12),
            Row("shared", "folder-b", 2026, 8, 12));

        var selection = IntelligenceCoverageCalculator.Resolve(
            inventory,
            [
                SemanticIndexFolderCoverageRule.Latest("folder-a", 2),
                SemanticIndexFolderCoverageRule.Latest("folder-b", 1),
            ],
            Now);

        selection.Folders[0].SelectedMessageCount.Should().Be(2);
        selection.Folders[1].SelectedMessageCount.Should().Be(1);
        // "shared" is selected by both rules but is one message.
        selection.DistinctSelectedCount.Should().Be(2);
        selection.ToRemoteMessageIds().Should().Equal("shared", "b");
    }

    [Fact]
    public void Resolve_OnlyNewPreset_SelectsNothingThatAlreadyExists()
    {
        var inventory = Build(
            Row("a", "folder", 2026, 8, 1),
            Row("b", "folder", 2026, 8, 16));

        var selection = IntelligenceCoverageCalculator.Resolve(
            inventory,
            [SemanticIndexFolderCoverageRule.DateRange("folder", SemanticIndexRangePreset.OnlyNew, null, null)],
            Now);

        selection.DistinctSelectedCount.Should().Be(0);
        selection.Folders[0].ReachDate.Should().BeNull();
    }

    [Fact]
    public void Resolve_EverythingPreset_SelectsTheWholeFolder()
    {
        var inventory = Build(
            Row("a", "folder", 2020, 1, 1),
            Row("b", "folder", 2026, 8, 16));

        var selection = IntelligenceCoverageCalculator.Resolve(
            inventory,
            [SemanticIndexFolderCoverageRule.DateRange("folder", SemanticIndexRangePreset.Everything, null, null)],
            Now);

        selection.DistinctSelectedCount.Should().Be(2);
    }

    [Fact]
    public void Resolve_DerivesThePresetCutoffFromTheSuppliedClockOnly()
    {
        var inventory = Build(
            Row("recent", "folder", 2026, 8, 14),
            Row("old", "folder", 2026, 8, 1));

        var selection = IntelligenceCoverageCalculator.Resolve(
            inventory,
            [SemanticIndexFolderCoverageRule.DateRange("folder", SemanticIndexRangePreset.OneWeek, null, null)],
            Now);

        selection.DistinctSelectedCount.Should().Be(1);
        selection.ToRemoteMessageIds().Should().Equal("recent");
    }

    [Fact]
    public void BuildIndexBitmap_SetsOnlyKnownIds()
    {
        var inventory = Build(
            Row("a", "folder", 2026, 8, 10),
            Row("b", "folder", 2026, 8, 11));

        var bitmap = IntelligenceCoverageCalculator.BuildIndexBitmap(
            inventory, Ids("b", "never-seen"));

        // "b" is the newer of the two, so it leads the newest-first order.
        bitmap[0].Should().BeTrue();
        bitmap[1].Should().BeFalse();
    }

    [Fact]
    public void BuildBuckets_PartitionsEveryMessageAcrossTheThreeBands()
    {
        var inventory = BuildRange("folder", 40);
        var selection = IntelligenceCoverageCalculator.Resolve(
            inventory, [SemanticIndexFolderCoverageRule.Latest("folder", 25)], Now);
        var indexed = IntelligenceCoverageCalculator.BuildIndexBitmap(inventory, Ids("message-0", "message-1"));

        var histogram = IntelligenceCoverageCalculator.BuildBuckets(
            inventory, Ids("folder"), selection.SelectedByIndex, indexed, 8);

        histogram.MessageCount.Should().Be(40);
        histogram.IndexedCount.Should().Be(2);
        histogram.SelectedNotIndexedCount.Should().Be(23);
        histogram.OutsideCount.Should().Be(15);
        histogram.Buckets.Sum(bucket => bucket.MessageCount).Should().Be(40);
        histogram.Buckets.Should().HaveCount(8);
    }

    [Fact]
    public void BuildBuckets_CountsAnIndexedMessageAsIndexedEvenWhenTheRuleExcludesIt()
    {
        var inventory = BuildRange("folder", 10);
        var selection = IntelligenceCoverageCalculator.Resolve(
            inventory, [SemanticIndexFolderCoverageRule.Latest("folder", 2)], Now);

        // message-9 is the oldest, so a "latest 2" rule leaves it out — but it stays indexed.
        var indexed = IntelligenceCoverageCalculator.BuildIndexBitmap(inventory, Ids("message-9"));

        var histogram = IntelligenceCoverageCalculator.BuildBuckets(
            inventory, Ids("folder"), selection.SelectedByIndex, indexed, 5);

        histogram.IndexedCount.Should().Be(1);
        histogram.SelectedNotIndexedCount.Should().Be(2);
        histogram.OutsideCount.Should().Be(7);
    }

    [Fact]
    public void BuildBuckets_NeverEmitsAnEmptyColumnWhenBucketsOutnumberMessages()
    {
        var inventory = BuildRange("folder", 3);
        var selection = IntelligenceCoverageCalculator.Resolve(inventory, [], Now);
        var indexed = IntelligenceCoverageCalculator.BuildIndexBitmap(inventory, Ids());

        var histogram = IntelligenceCoverageCalculator.BuildBuckets(
            inventory, Ids("folder"), selection.SelectedByIndex, indexed, 32);

        histogram.Buckets.Should().HaveCount(3);
        histogram.Buckets.Should().OnlyContain(bucket => bucket.MessageCount == 1);
        histogram.BusiestBucketCount.Should().Be(1);
    }

    [Fact]
    public void BuildBuckets_CountsAMessageInTwoSelectedFoldersOnce()
    {
        var inventory = Build(
            Row("shared", "folder-a", 2026, 8, 12),
            Row("shared", "folder-b", 2026, 8, 12),
            Row("only-a", "folder-a", 2026, 8, 11));
        var selection = IntelligenceCoverageCalculator.Resolve(inventory, [], Now);
        var indexed = IntelligenceCoverageCalculator.BuildIndexBitmap(inventory, Ids());

        var histogram = IntelligenceCoverageCalculator.BuildBuckets(
            inventory, Ids("folder-a", "folder-b"), selection.SelectedByIndex, indexed, 4);

        histogram.MessageCount.Should().Be(2);
    }

    [Fact]
    public void BuildBuckets_ReturnsEmptyForFoldersWithNoMail()
    {
        var inventory = BuildRange("folder", 5);
        var selection = IntelligenceCoverageCalculator.Resolve(inventory, [], Now);
        var indexed = IntelligenceCoverageCalculator.BuildIndexBitmap(inventory, Ids());

        var histogram = IntelligenceCoverageCalculator.BuildBuckets(
            inventory, Ids("absent-folder"), selection.SelectedByIndex, indexed, 8);

        histogram.Should().BeSameAs(IntelligenceCoverageBuckets.Empty);
        histogram.BusiestBucketCount.Should().Be(0);
    }

    private static IReadOnlySet<string> Ids(params string[] values)
        => new HashSet<string>(values, StringComparer.Ordinal);

    /// <summary>A folder of <paramref name="count"/> messages, one per day, newest first.</summary>
    private static IntelligenceCoverageInventory BuildRange(string remoteFolderId, int count)
        => Build([.. Enumerable.Range(0, count).Select(offset => new IntelligenceCoverageInventoryRow(
            $"message-{offset}",
            Now.AddDays(-offset),
            remoteFolderId))]);

    private static IntelligenceCoverageInventory Build(params IntelligenceCoverageInventoryRow[] rows)
        => IntelligenceCoverageInventory.Create(Guid.NewGuid(), rows);

    private static IntelligenceCoverageInventoryRow Row(
        string remoteMessageId, string remoteFolderId, int year, int month, int day, int hour = 0)
        => new(remoteMessageId, new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero), remoteFolderId);

}
