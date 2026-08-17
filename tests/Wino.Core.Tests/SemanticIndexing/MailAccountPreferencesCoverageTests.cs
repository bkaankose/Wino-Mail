using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.SemanticIndexing;
using Xunit;

namespace Wino.Core.Tests.SemanticIndexing;

public sealed class MailAccountPreferencesCoverageTests
{
    [Fact]
    public void DefaultCoverageRule_WithoutStoredValue_IsLatestHundred()
    {
        var preferences = new MailAccountPreferences();

        var rule = preferences.IntelligenceDefaultCoverageRule;

        rule.Mode.Should().Be(SemanticIndexCoverageMode.LatestCount);
        rule.LatestMessageCount.Should().Be(MailAccountPreferences.DefaultLatestMessageCount);
        rule.RemoteFolderId.Should().BeEmpty();
    }

    [Fact]
    public void DefaultCoverageRule_RoundTripsThroughStorage()
    {
        var preferences = new MailAccountPreferences
        {
            IntelligenceDefaultCoverageRule = SemanticIndexFolderCoverageRule.DateRange(
                "ignored", SemanticIndexRangePreset.SixMonths, null, null),
        };

        preferences.PrepareForStorage();
        var reloaded = new MailAccountPreferences
        {
            IntelligenceDefaultCoverageStorage = preferences.IntelligenceDefaultCoverageStorage,
        };

        var rule = reloaded.IntelligenceDefaultCoverageRule;
        rule.Mode.Should().Be(SemanticIndexCoverageMode.DateRange);
        rule.DatePreset.Should().Be(SemanticIndexRangePreset.SixMonths);
        // The default belongs to no folder, so a folder id must never survive on it.
        rule.RemoteFolderId.Should().BeEmpty();
    }

    [Fact]
    public void DefaultCoverageRule_WithCorruptStorage_FallsBackInsteadOfThrowing()
    {
        var preferences = new MailAccountPreferences { IntelligenceDefaultCoverageStorage = "{ not json" };

        var rule = preferences.IntelligenceDefaultCoverageRule;

        rule.LatestMessageCount.Should().Be(MailAccountPreferences.DefaultLatestMessageCount);
    }

    [Fact]
    public void PrepareForStorage_KeepsFolderRulesAndDefaultIndependent()
    {
        var preferences = new MailAccountPreferences();
        preferences.IntelligenceFolderCoverageRules["inbox"] = SemanticIndexFolderCoverageRule.Latest("inbox", 25);
        preferences.IntelligenceDefaultCoverageRule = SemanticIndexFolderCoverageRule.Latest(string.Empty, 500);

        preferences.PrepareForStorage();
        var reloaded = new MailAccountPreferences
        {
            IntelligenceFolderCoverageStorage = preferences.IntelligenceFolderCoverageStorage,
            IntelligenceDefaultCoverageStorage = preferences.IntelligenceDefaultCoverageStorage,
        };

        reloaded.IntelligenceFolderCoverageRules["inbox"].LatestMessageCount.Should().Be(25);
        reloaded.IntelligenceDefaultCoverageRule.LatestMessageCount.Should().Be(500);
    }
}
