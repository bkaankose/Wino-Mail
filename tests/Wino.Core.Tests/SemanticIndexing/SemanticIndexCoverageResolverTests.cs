using FluentAssertions;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.AI.Abstractions;
using Xunit;

namespace Wino.Core.Tests.SemanticIndexing;

public sealed class SemanticIndexCoverageResolverTests
{
    [Fact]
    public void LatestCount_IsDeterministicAndDeduplicatesAcrossFolders()
    {
        var candidates = new[]
        {
            Candidate("a", "folder-a", new DateTime(2026, 8, 10)),
            Candidate("b", "folder-a", new DateTime(2026, 8, 11)),
            Candidate("shared", "folder-a", new DateTime(2026, 8, 12), "folder-b"),
        };
        var rules = new[]
        {
            SemanticIndexFolderCoverageRule.Latest("folder-a", 2),
            SemanticIndexFolderCoverageRule.Latest("folder-b", 1),
        };

        var result = SemanticIndexCoverageResolver.Resolve(candidates, rules);

        result.Candidates.Select(x => x.RemoteMessageId).Should().Equal("shared", "b");
        result.FolderPlans[0].EligibleMessageCount.Should().Be(2);
        result.FolderPlans[1].EligibleMessageCount.Should().Be(1);
    }

    [Fact]
    public void DateRange_UsesInclusiveStartAndExclusiveEnd()
    {
        var candidates = new[]
        {
            Candidate("before", "folder", new DateTime(2026, 8, 1)),
            Candidate("inside", "folder", new DateTime(2026, 8, 2)),
            Candidate("after", "folder", new DateTime(2026, 8, 3)),
        };
        var rule = SemanticIndexFolderCoverageRule.DateRange(
            "folder", SemanticIndexRangePreset.Custom,
            new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero));

        var result = SemanticIndexCoverageResolver.Resolve(candidates, [rule]);

        result.Candidates.Should().ContainSingle().Which.RemoteMessageId.Should().Be("inside");
    }

    private static IntelligenceMessageCandidate Candidate(string id, string folder, DateTime receivedAt, params string[] additionalFolders)
        => new(Guid.NewGuid(), id, id, [], string.Empty, string.Empty, string.Empty, receivedAt, null,
            false, false, false, false, false, "normal", [folder, .. additionalFolders],
            new MailBodyLocator(id, folder, 0, 0, id));
}
