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
    public void CountMissing_IntersectsTheSelectionWithTheDelta()
    {
        var inventory = Build(
            Row("a", "folder", 2026, 8, 10),
            Row("b", "folder", 2026, 8, 11),
            Row("c", "folder", 2026, 8, 12));
        var missing = new System.Collections.BitArray(inventory.TotalMessageCount);
        // "c" is newest so it sits at index 0; mark it and the oldest as absent from the cloud.
        missing[0] = true;
        missing[2] = true;
        var delta = new IntelligenceCoverageDelta(inventory.LocalAccountId, inventory.BuiltAtUtc, missing, Now);

        var selection = IntelligenceCoverageCalculator.Resolve(
            inventory, [SemanticIndexFolderCoverageRule.Latest("folder", 2)], Now);

        selection.CountMissing(delta).Should().Be(1);
        selection.CountMissing(delta, selection.Folders[0]).Should().Be(1);
        delta.Matches(inventory).Should().BeTrue();
    }

    [Fact]
    public void BuildDailyCounts_CountsEachMessageOncePerDayAcrossFolders()
    {
        var inventory = Build(
            Row("a", "folder-a", 2026, 8, 10),
            Row("b", "folder-a", 2026, 8, 10, hour: 20),
            Row("shared", "folder-a", 2026, 8, 11),
            Row("shared", "folder-b", 2026, 8, 11),
            Row("elsewhere", "folder-c", 2026, 8, 11));

        var counts = IntelligenceCoverageCalculator.BuildDailyCounts(
            inventory, new HashSet<string>(StringComparer.Ordinal) { "folder-a", "folder-b" });

        counts[new DateOnly(2026, 8, 10)].Should().Be(2);
        counts[new DateOnly(2026, 8, 11)].Should().Be(1);
        counts.Should().HaveCount(2);
    }

    /// <summary>
    /// The calculator estimates what the backfill worker will index, and the worker still resolves
    /// its own candidates through <see cref="SemanticIndexCoverageResolver"/>. If the two ever pick
    /// different messages the page lies about the plan, so they are pinned to each other here.
    /// </summary>
    [Theory]
    [MemberData(nameof(EquivalenceRuleSets))]
    public void Resolve_MatchesSemanticIndexCoverageResolver(SemanticIndexFolderCoverageRule[] rules)
    {
        var candidates = new[]
        {
            Candidate("m1", "folder-a", new DateTime(2026, 8, 16, 9, 0, 0)),
            Candidate("m2", "folder-a", new DateTime(2026, 8, 14, 9, 0, 0)),
            // Same instant as m2: the ordinal id tie-break decides which one a latest-N rule takes.
            Candidate("m3", "folder-a", new DateTime(2026, 8, 14, 9, 0, 0)),
            Candidate("m4", "folder-a", new DateTime(2026, 5, 1, 9, 0, 0)),
            Candidate("m5", "folder-b", new DateTime(2026, 8, 15, 9, 0, 0)),
            Candidate("shared", "folder-a", new DateTime(2026, 8, 10, 9, 0, 0), "folder-b"),
        };
        var inventory = IntelligenceCoverageInventory.Create(
            Guid.Empty,
            candidates.SelectMany(candidate => candidate.RemoteFolderIds.Select(folder =>
                new IntelligenceCoverageInventoryRow(
                    candidate.RemoteMessageId,
                    new DateTimeOffset(DateTime.SpecifyKind(candidate.ReceivedAt, DateTimeKind.Utc)),
                    folder))));

        var expected = SemanticIndexCoverageResolver.Resolve(candidates, rules);
        var actual = IntelligenceCoverageCalculator.Resolve(inventory, rules, Now);

        actual.ToRemoteMessageIds().Should().Equal(expected.Candidates.Select(x => x.RemoteMessageId));
        actual.DistinctSelectedCount.Should().Be(expected.EligibleMessageCount);
        for (var index = 0; index < rules.Length; index++)
        {
            actual.Folders[index].SelectedMessageCount.Should()
                .Be(expected.FolderPlans[index].EligibleMessageCount, "folder {0} must agree", rules[index].RemoteFolderId);
            actual.Folders[index].AvailableMessageCount.Should()
                .Be(expected.FolderPlans[index].AvailableMessageCount);
        }
    }

    public static TheoryData<SemanticIndexFolderCoverageRule[]> EquivalenceRuleSets() =>
    [
        [SemanticIndexFolderCoverageRule.Latest("folder-a", 3), SemanticIndexFolderCoverageRule.Latest("folder-b", 1)],
        [SemanticIndexFolderCoverageRule.Latest("folder-a", 0), SemanticIndexFolderCoverageRule.Latest("folder-b", 99)],
        [
            SemanticIndexFolderCoverageRule.DateRange("folder-a", SemanticIndexRangePreset.OneMonth, null, null),
            SemanticIndexFolderCoverageRule.DateRange("folder-b", SemanticIndexRangePreset.Everything, null, null),
        ],
        [
            SemanticIndexFolderCoverageRule.DateRange("folder-a", SemanticIndexRangePreset.Custom,
                new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.Zero)),
            SemanticIndexFolderCoverageRule.Latest("folder-b", 2),
        ],
    ];

    private static IntelligenceCoverageInventory Build(params IntelligenceCoverageInventoryRow[] rows)
        => IntelligenceCoverageInventory.Create(Guid.NewGuid(), rows);

    private static IntelligenceCoverageInventoryRow Row(
        string remoteMessageId, string remoteFolderId, int year, int month, int day, int hour = 0)
        => new(remoteMessageId, new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero), remoteFolderId);

    private static IntelligenceMessageCandidate Candidate(
        string id, string folder, DateTime receivedAt, params string[] additionalFolders)
        => new(Guid.NewGuid(), id, id, [], string.Empty, string.Empty, string.Empty, receivedAt, null,
            false, false, false, false, false, "normal", [folder, .. additionalFolders],
            new MailBodyLocator(id, folder, 0, 0, id));
}
