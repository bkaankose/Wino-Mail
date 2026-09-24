using System;
using FluentAssertions;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Intelligence;

/// <summary>
/// The device result key rule, row by row. The lifecycle table on the key lifecycle plan is the spec.
/// </summary>
public sealed class IntelligenceResultKeyPolicyTests
{
    private static readonly Guid UserId = Guid.Parse("0f0e0d0c-0b0a-0908-0706-050403020100");

    [Theory]
    // Active is the only state that ever creates a key, with or without one already present.
    [InlineData(WinoIntelligenceEntitlementState.Active, true, false, IntelligenceResultKeyAction.Ensure)]
    [InlineData(WinoIntelligenceEntitlementState.Active, true, true, IntelligenceResultKeyAction.Ensure)]
    [InlineData(WinoIntelligenceEntitlementState.Active, false, false, IntelligenceResultKeyAction.Ensure)]
    // Quota exhaustion and an unresponsive API keep everything, however long they last.
    [InlineData(WinoIntelligenceEntitlementState.QuotaExhausted, true, true, IntelligenceResultKeyAction.None)]
    [InlineData(WinoIntelligenceEntitlementState.QuotaExhausted, true, false, IntelligenceResultKeyAction.None)]
    [InlineData(WinoIntelligenceEntitlementState.Unavailable, false, true, IntelligenceResultKeyAction.None)]
    [InlineData(WinoIntelligenceEntitlementState.Unavailable, true, true, IntelligenceResultKeyAction.None)]
    // Only an authoritative end of the add-on removes the key.
    [InlineData(WinoIntelligenceEntitlementState.Expired, true, true, IntelligenceResultKeyAction.Remove)]
    [InlineData(WinoIntelligenceEntitlementState.Expired, false, true, IntelligenceResultKeyAction.None)]
    [InlineData(WinoIntelligenceEntitlementState.NoSubscription, true, true, IntelligenceResultKeyAction.Remove)]
    [InlineData(WinoIntelligenceEntitlementState.NoSubscription, false, true, IntelligenceResultKeyAction.None)]
    // Nothing to remove means nothing is touched: users without the add-on never reach the store.
    [InlineData(WinoIntelligenceEntitlementState.Expired, true, false, IntelligenceResultKeyAction.None)]
    [InlineData(WinoIntelligenceEntitlementState.NoSubscription, true, false, IntelligenceResultKeyAction.None)]
    public void Decide_FollowsTheLifecycleTable(
        WinoIntelligenceEntitlementState state,
        bool isAuthoritative,
        bool keyMayExist,
        IntelligenceResultKeyAction expected)
    {
        var snapshot = new WinoIntelligenceEntitlementSnapshot(state, UserId, DateTimeOffset.UtcNow) { IsAuthoritative = isAuthoritative };

        IntelligenceResultKeyPolicy.Decide(snapshot, keyMayExist).Should().Be(expected);
    }

    [Fact]
    public void SignedOut_RemovesAKeyThatMayExist()
    {
        var snapshot = WinoIntelligenceEntitlementSnapshot.SignedOut(DateTimeOffset.UtcNow);

        IntelligenceResultKeyPolicy.Decide(snapshot, keyMayExist: true).Should().Be(IntelligenceResultKeyAction.Remove);
        IntelligenceResultKeyPolicy.Decide(snapshot, keyMayExist: false).Should().Be(IntelligenceResultKeyAction.None);
    }

    [Fact]
    public void ActiveWithoutAWinoAccount_CreatesNothing()
    {
        var snapshot = new WinoIntelligenceEntitlementSnapshot(WinoIntelligenceEntitlementState.Active, null, DateTimeOffset.UtcNow);

        IntelligenceResultKeyPolicy.Decide(snapshot, keyMayExist: false).Should().Be(IntelligenceResultKeyAction.None);
    }

    [Fact]
    public void Evaluate_IsAuthoritativeOnlyForFreshBilling()
    {
        var fresh = WinoIntelligenceEntitlementSnapshot.Evaluate(UserId, null, null, DateTimeOffset.UtcNow, isFreshBilling: true);
        var cached = WinoIntelligenceEntitlementSnapshot.Evaluate(UserId, null, null, DateTimeOffset.UtcNow, isFreshBilling: false);

        fresh.State.Should().Be(WinoIntelligenceEntitlementState.NoSubscription);
        fresh.IsAuthoritative.Should().BeTrue();
        cached.State.Should().Be(WinoIntelligenceEntitlementState.Unavailable);
        cached.IsAuthoritative.Should().BeFalse();
    }
}
