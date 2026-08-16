using FluentAssertions;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class SemanticIndexRangeBucketViewModelTests
{
    [Fact]
    public void StackedCoverage_UsesTruthCountsAndPreservesTotalHeight()
    {
        var bucket = Create(localAndCloud: 4, localOnly: 2, cloudOnly: 1, empty: 1, height: 64);

        bucket.LocalAndCloudHeight.Should().Be(32);
        bucket.LocalOnlyHeight.Should().Be(16);
        bucket.CloudOnlyHeight.Should().Be(8);
        bucket.EmptyHeight.Should().Be(8);
        (bucket.LocalAndCloudHeight + bucket.LocalOnlyHeight + bucket.CloudOnlyHeight + bucket.EmptyHeight).Should().Be(64);
    }

    [Fact]
    public void EmptyCoverage_UsesNeutralFullHeightSegment()
    {
        var bucket = Create(localAndCloud: 0, localOnly: 0, cloudOnly: 0, empty: 0, height: 2);

        bucket.IsEmpty.Should().BeTrue();
        bucket.EmptyHeight.Should().Be(2);
        bucket.LocalAndCloudHeight.Should().Be(0);
        bucket.LocalOnlyHeight.Should().Be(0);
        bucket.CloudOnlyHeight.Should().Be(0);
    }

    [Fact]
    public void Classifier_DownloadedCoverageBecomesCloudOnlyAfterLocalDeletion()
    {
        string[] messages = ["both", "cloud-only", "empty"];
        var cloudMissing = new HashSet<string>(["empty"], StringComparer.Ordinal);

        var beforeDeletion = SemanticIndexCoverageClassifier.Classify(
            messages,
            new HashSet<string>(["both"], StringComparer.Ordinal),
            cloudMissing,
            cloudIndexedMessageCount: 2);
        var afterDeletion = SemanticIndexCoverageClassifier.Classify(
            messages,
            new HashSet<string>(StringComparer.Ordinal),
            cloudMissing,
            cloudIndexedMessageCount: 2);

        beforeDeletion.Should().Be(new SemanticIndexCoverageCounts(3, 1, 0, 1, 1));
        afterDeletion.Should().Be(new SemanticIndexCoverageCounts(3, 0, 0, 2, 1));
    }

    [Fact]
    public void Classifier_LocalArtifactWithoutServerCoverageIsLocalOnly()
    {
        var counts = SemanticIndexCoverageClassifier.Classify(
            ["local-only"],
            new HashSet<string>(["local-only"], StringComparer.Ordinal),
            new HashSet<string>(["local-only"], StringComparer.Ordinal),
            cloudIndexedMessageCount: 0);

        counts.Should().Be(new SemanticIndexCoverageCounts(1, 0, 1, 0, 0));
    }

    private static SemanticIndexRangeBucketViewModel Create(int localAndCloud, int localOnly, int cloudOnly, int empty, double height)
        => new()
        {
            StartOffset = 0,
            EndOffset = 0,
            StartDate = new DateOnly(2026, 8, 15),
            EndDate = new DateOnly(2026, 8, 15),
            MessageCount = localAndCloud + localOnly + cloudOnly + empty,
            LocalAndCloudCount = localAndCloud,
            LocalOnlyCount = localOnly,
            CloudOnlyCount = cloudOnly,
            EmptyCount = empty,
            BarHeight = height,
            BarWidth = 8,
        };
}
