using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.SemanticIndexing;
using Xunit;

namespace Wino.Core.Tests.SemanticIndexing;

/// <summary>
/// Coverage rules used to live only in memory and were lost on every navigation. These pin the
/// storage format that keeps them.
/// </summary>
public sealed class MailAccountPreferencesCoverageTests
{
    [Fact]
    public void PrepareForStorage_RoundTripsPerFolderRules()
    {
        var cutoff = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var through = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var preferences = new MailAccountPreferences
        {
            IntelligenceFolderCoverageRules =
            [
                SemanticIndexFolderCoverageRule.Latest("inbox", 500),
                SemanticIndexFolderCoverageRule.DateRange("archive", SemanticIndexRangePreset.Custom, cutoff, through),
            ],
        };

        preferences.PrepareForStorage();
        var restored = new MailAccountPreferences
        {
            IntelligenceFolderCoverageStorage = preferences.IntelligenceFolderCoverageStorage,
        };

        restored.IntelligenceFolderCoverageRules.Should().HaveCount(2);
        restored.IntelligenceFolderCoverageRules[0].Should().Be(
            SemanticIndexFolderCoverageRule.Latest("inbox", 500));
        restored.IntelligenceFolderCoverageRules[1].Should().Be(
            SemanticIndexFolderCoverageRule.DateRange("archive", SemanticIndexRangePreset.Custom, cutoff, through));
    }

    [Fact]
    public void PrepareForStorage_RoundTripsTheDefaultRuleWithoutAFolderId()
    {
        var preferences = new MailAccountPreferences
        {
            IntelligenceDefaultCoverageRule = SemanticIndexFolderCoverageRule.Latest("ignored", 250),
        };

        preferences.PrepareForStorage();
        var restored = new MailAccountPreferences
        {
            IntelligenceDefaultCoverageStorage = preferences.IntelligenceDefaultCoverageStorage,
        };

        restored.IntelligenceDefaultCoverageRule.RemoteFolderId.Should().BeEmpty();
        restored.IntelligenceDefaultCoverageRule.LatestMessageCount.Should().Be(250);
    }

    [Fact]
    public void DefaultCoverageRule_FallsBackWhenNothingIsStored()
    {
        var preferences = new MailAccountPreferences();

        preferences.IntelligenceDefaultCoverageRule.Mode.Should().Be(SemanticIndexCoverageMode.LatestCount);
        preferences.IntelligenceDefaultCoverageRule.LatestMessageCount
            .Should().Be(MailAccountPreferences.DefaultLatestMessageCount);
        preferences.IntelligenceFolderCoverageRules.Should().BeEmpty();
    }

    [Fact]
    public void Deserialize_KeepsFolderIdsThatContainTheFieldSeparator()
    {
        // Provider folder ids are opaque. The id is the last field precisely so one containing a
        // separator survives instead of truncating.
        var preferences = new MailAccountPreferences
        {
            IntelligenceFolderCoverageRules = [SemanticIndexFolderCoverageRule.Latest("weird|id|here", 10)],
        };
        preferences.PrepareForStorage();

        var restored = new MailAccountPreferences
        {
            IntelligenceFolderCoverageStorage = preferences.IntelligenceFolderCoverageStorage,
        };

        restored.IntelligenceFolderCoverageRules.Should().ContainSingle()
            .Which.RemoteFolderId.Should().Be("weird|id|here");
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("count|one-month|||")]
    [InlineData("\n\n")]
    public void Deserialize_SkipsMalformedLinesRatherThanThrowing(string storage)
    {
        var preferences = new MailAccountPreferences { IntelligenceFolderCoverageStorage = storage };

        preferences.IntelligenceFolderCoverageRules.Should().BeEmpty();
    }

    [Fact]
    public void Deserialize_KeepsGoodLinesAlongsideMalformedOnes()
    {
        var preferences = new MailAccountPreferences
        {
            IntelligenceFolderCoverageStorage = "garbage\ncount|one-month|||500|inbox",
        };

        preferences.IntelligenceFolderCoverageRules.Should().ContainSingle()
            .Which.Should().Be(SemanticIndexFolderCoverageRule.Latest("inbox", 500));
    }

    [Fact]
    public void Deserialize_TreatsAnUnknownPresetIdAsOnlyNew()
    {
        var preferences = new MailAccountPreferences
        {
            IntelligenceFolderCoverageStorage = "date|from-the-future|||0|inbox",
        };

        preferences.IntelligenceFolderCoverageRules.Should().ContainSingle()
            .Which.DatePreset.Should().Be(SemanticIndexRangePreset.OnlyNew);
    }
}
