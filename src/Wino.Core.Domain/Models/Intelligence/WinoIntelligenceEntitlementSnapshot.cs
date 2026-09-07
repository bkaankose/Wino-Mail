#nullable enable
using System;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Core.Domain.Models.Intelligence;

public enum WinoIntelligenceEntitlementState
{
    SignedOut,
    NoSubscription,
    Expired,
    Active,
    QuotaExhausted,
    Unavailable,
}

public sealed record WinoIntelligenceEntitlementSnapshot(
    WinoIntelligenceEntitlementState State,
    Guid? WinoAccountId,
    DateTimeOffset EvaluatedAtUtc)
{
    public bool CanAccessSurfaces => State is WinoIntelligenceEntitlementState.Active
        or WinoIntelligenceEntitlementState.QuotaExhausted;

    public bool CanConsumeQuota => State == WinoIntelligenceEntitlementState.Active;

    public static WinoIntelligenceEntitlementSnapshot SignedOut(DateTimeOffset now)
        => new(WinoIntelligenceEntitlementState.SignedOut, null, now);

    public static WinoIntelligenceEntitlementSnapshot Evaluate(
        Guid accountId,
        BillingStatusResultDto? billing,
        AiUsageStatusDto? usage,
        DateTimeOffset now,
        bool isFreshBilling)
    {
        var aiPack = billing?.AiPack;
        if (aiPack is null)
            return new(isFreshBilling ? WinoIntelligenceEntitlementState.NoSubscription : WinoIntelligenceEntitlementState.Unavailable, accountId, now);

        var periodEnded = aiPack.CurrentPeriodEndUtc is DateTimeOffset periodEnd && periodEnd <= now;
        var statusExpired = string.Equals(aiPack.Status, "expired", StringComparison.OrdinalIgnoreCase);
        if (periodEnded || statusExpired)
            return new(WinoIntelligenceEntitlementState.Expired, accountId, now);

        // A cached response is safe only when it carries a future period boundary.
        if (!isFreshBilling && aiPack.CurrentPeriodEndUtc is null)
            return new(WinoIntelligenceEntitlementState.Unavailable, accountId, now);

        if (!aiPack.HasAccess)
            return new(WinoIntelligenceEntitlementState.NoSubscription, accountId, now);

        var quotaExhausted = usage is not null &&
            (usage.IsExhausted || usage.RemainingPercentage <= 0 || usage.UsagePercentage >= 100);
        return new(
            quotaExhausted ? WinoIntelligenceEntitlementState.QuotaExhausted : WinoIntelligenceEntitlementState.Active,
            accountId,
            now);
    }
}
