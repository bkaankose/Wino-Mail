using FluentAssertions;
using Wino.Core.Services;
using Xunit;

namespace Wino.Core.Tests.Exchange;

public class PushAwarePollPolicyTests
{
    private readonly Guid _account = Guid.NewGuid();

    [Fact]
    public void FirstTick_AlwaysPolls()
    {
        var policy = new PushAwarePollPolicy();

        policy.Next(_account, isPushConnected: true, interruptionCount: 0).Should().Be(PollDecision.Poll);
    }

    [Fact]
    public void OpenChannel_SkipsThePollAndReconcilesAsTheBackstop()
    {
        var policy = new PushAwarePollPolicy(backstopEveryNTicks: 3);
        policy.Next(_account, true, 0);

        var decisions = Enumerable.Range(0, 6).Select(_ => policy.Next(_account, true, 0)).ToList();

        decisions.Should().Equal(
            PollDecision.Skip, PollDecision.Skip, PollDecision.Reconcile,
            PollDecision.Skip, PollDecision.Skip, PollDecision.Reconcile);
    }

    [Fact]
    public void ClosedChannel_TightensAtOnceAndKeepsPolling()
    {
        var policy = new PushAwarePollPolicy();
        policy.Next(_account, true, 0);
        policy.Next(_account, true, 0).Should().Be(PollDecision.Skip);

        // The tick that finds the channel closed catches up; the ordinary poll follows.
        policy.Next(_account, false, 1).Should().Be(PollDecision.Reconcile);
        policy.Next(_account, false, 1).Should().Be(PollDecision.Poll);
        policy.Next(_account, false, 1).Should().Be(PollDecision.Poll);
    }

    [Fact]
    public void ChannelComingBack_ReconcilesOnceThenRelaxesAgain()
    {
        var policy = new PushAwarePollPolicy();
        policy.Next(_account, false, 0);
        policy.Next(_account, false, 0).Should().Be(PollDecision.Poll);

        policy.Next(_account, true, 0).Should().Be(PollDecision.Reconcile);
        policy.Next(_account, true, 0).Should().Be(PollDecision.Skip);
    }

    [Fact]
    public void DropBetweenTwoTicks_IsNoticedThroughTheInterruptionCount()
    {
        var policy = new PushAwarePollPolicy();
        policy.Next(_account, true, 4);
        policy.Next(_account, true, 4).Should().Be(PollDecision.Skip);

        // Open at both ticks, but it closed and reopened in between: nothing was pushed meanwhile.
        policy.Next(_account, true, 5).Should().Be(PollDecision.Reconcile);
        policy.Next(_account, true, 5).Should().Be(PollDecision.Skip);

        // A restarted listener counts from zero again.
        policy.Next(_account, true, 0).Should().Be(PollDecision.Reconcile);
    }

    [Fact]
    public void Accounts_AreTrackedSeparately_AndForgottenWhenRemoved()
    {
        var policy = new PushAwarePollPolicy();
        var other = Guid.NewGuid();
        policy.Next(_account, true, 0);
        policy.Next(other, false, 0);

        policy.Next(_account, true, 0).Should().Be(PollDecision.Skip);
        policy.Next(other, false, 0).Should().Be(PollDecision.Poll);

        policy.Retain([other]);

        policy.Next(_account, true, 0).Should().Be(PollDecision.Poll);
    }

    [Fact]
    public void Backstop_MustBePositive()
    {
        var act = () => new PushAwarePollPolicy(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
