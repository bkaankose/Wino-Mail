using FluentAssertions;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Contracts.Intelligence;
using Xunit;

namespace Wino.Core.Tests.Models;

public sealed class WinoIntelligenceEntitlementSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AccountId = Guid.NewGuid();

    [Fact]
    public void SignedOut_DeniesSurfacesAndQuota()
    {
        var entitlement = WinoIntelligenceEntitlementSnapshot.SignedOut(Now);

        entitlement.State.Should().Be(WinoIntelligenceEntitlementState.SignedOut);
        entitlement.CanAccessSurfaces.Should().BeFalse();
        entitlement.CanConsumeQuota.Should().BeFalse();
    }

    [Fact]
    public void FreshBillingWithoutAiPack_IsNoSubscription()
        => Evaluate(null, null, isFreshBilling: true).State
            .Should().Be(WinoIntelligenceEntitlementState.NoSubscription);

    [Fact]
    public void ActiveSubscription_AllowsSurfacesAndQuota()
    {
        var entitlement = Evaluate(Billing(hasAccess: true, periodEnd: Now.AddDays(30)));

        entitlement.State.Should().Be(WinoIntelligenceEntitlementState.Active);
        entitlement.CanAccessSurfaces.Should().BeTrue();
        entitlement.CanConsumeQuota.Should().BeTrue();
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("active")]
    public void EndedSubscription_IsExpired(string status)
        => Evaluate(Billing(hasAccess: true, periodEnd: Now, status: status)).State
            .Should().Be(WinoIntelligenceEntitlementState.Expired);

    [Fact]
    public void CancellationAtPeriodEnd_RemainsActiveUntilPeriodEnds()
        => Evaluate(Billing(hasAccess: true, periodEnd: Now.AddDays(5), cancelAtPeriodEnd: true)).State
            .Should().Be(WinoIntelligenceEntitlementState.Active);

    [Fact]
    public void ValidCachedAccess_IsAccepted()
        => Evaluate(Billing(hasAccess: true, periodEnd: Now.AddDays(5)), isFreshBilling: false).State
            .Should().Be(WinoIntelligenceEntitlementState.Active);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrExpiredCachedPeriod_FailsClosed(bool expired)
    {
        var periodEnd = expired ? Now.AddSeconds(-1) : (DateTimeOffset?)null;

        Evaluate(Billing(hasAccess: true, periodEnd: periodEnd), isFreshBilling: false).State
            .Should().Be(expired
                ? WinoIntelligenceEntitlementState.Expired
                : WinoIntelligenceEntitlementState.Unavailable);
    }

    [Fact]
    public void UnavailableBilling_IsUnavailable()
        => Evaluate(null, null, isFreshBilling: false).State
            .Should().Be(WinoIntelligenceEntitlementState.Unavailable);

    [Fact]
    public void ConfirmedZeroQuota_KeepsSurfacesButPausesConsumption()
    {
        var entitlement = Evaluate(
            Billing(hasAccess: true, periodEnd: Now.AddDays(5)),
            new AiUsageStatusDto
            {
                EntitlementStatus = "active",
                RemainingPercentage = 0,
                UsagePercentage = 100,
                IsExhausted = true,
            });

        entitlement.State.Should().Be(WinoIntelligenceEntitlementState.QuotaExhausted);
        entitlement.CanAccessSurfaces.Should().BeTrue();
        entitlement.CanConsumeQuota.Should().BeFalse();
    }

    [Fact]
    public void MissingUsage_DoesNotRevokeAccess()
        => Evaluate(Billing(hasAccess: true, periodEnd: Now.AddDays(5)), usage: null).State
            .Should().Be(WinoIntelligenceEntitlementState.Active);

    private static WinoIntelligenceEntitlementSnapshot Evaluate(
        BillingStatusResultDto? billing,
        AiUsageStatusDto? usage = null,
        bool isFreshBilling = true)
        => WinoIntelligenceEntitlementSnapshot.Evaluate(AccountId, billing, usage, Now, isFreshBilling);

    private static BillingStatusResultDto Billing(
        bool hasAccess,
        DateTimeOffset? periodEnd,
        string status = "active",
        bool cancelAtPeriodEnd = false)
        => new(false, new AiPackBillingStatusDto(status, hasAccess, Now.AddDays(-1), periodEnd, periodEnd, cancelAtPeriodEnd));
}
