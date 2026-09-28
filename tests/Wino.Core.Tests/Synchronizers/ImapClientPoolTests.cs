using FluentAssertions;
using Wino.Core.Integration;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public class ImapClientPoolTests
{
    [Theory]
    [InlineData("imap.126.com", 30)]
    [InlineData("IMAP.126.COM.", 30)]
    [InlineData("126.com", 240)]
    [InlineData("other.imap.126.com", 240)]
    [InlineData("imap.163.com", 240)]
    [InlineData("imap.126.com.example.org", 240)]
    [InlineData("not126.com", 240)]
    public void KeepaliveExceptionIsLimitedToMeasuredEndpoint(string host, int seconds)
    {
        ImapServerQuirks.Resolve(host).KeepAliveInterval.Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [Theory]
    [InlineData("126.com", true)]
    [InlineData("other.imap.126.com", true)]
    [InlineData("IMAP.QQ.COM.", true)]
    [InlineData("imap.163.com", true)]
    [InlineData("imap.yeah.net", true)]
    [InlineData("not126.com", false)]
    [InlineData("imap.126.com.example.org", false)]
    public void DomainQuirksMatchOnlyTheirOwnDomain(string host, bool conservative)
    {
        ImapServerQuirks.Resolve(host).UseConservativeConnections.Should().Be(conservative);
    }

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
