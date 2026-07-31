using FluentAssertions;
using Wino.Core.Integration;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public class ImapClientPoolTests
{
    [Fact]
    public void CalculateMaxConnections_ShouldUseDefault_WhenConfiguredValueIsNonPositive()
    {
        ImapClientPool.CalculateMaxConnections(0).Should().Be(5);
        ImapClientPool.CalculateMaxConnections(-4).Should().Be(5);
    }

    [Fact]
    public void CalculateMaxConnections_ShouldClampToTen_WhenConfiguredValueIsTooHigh()
    {
        ImapClientPool.CalculateMaxConnections(40).Should().Be(10);
    }

    [Fact]
    public void CalculateTargetMinimumConnections_ShouldRespectConservativeMode()
    {
        ImapClientPool.CalculateTargetMinimumConnections(maxConnections: 5, useConservativeConnections: true).Should().Be(1);
    }

    [Fact]
    public void CalculateTargetMinimumConnections_ShouldBeTwo_WhenNotConservativeAndCapacityAllows()
    {
        ImapClientPool.CalculateTargetMinimumConnections(maxConnections: 5, useConservativeConnections: false).Should().Be(2);
    }
}
