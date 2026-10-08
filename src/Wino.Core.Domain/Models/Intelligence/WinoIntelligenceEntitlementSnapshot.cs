#nullable enable
using System;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Api.Contracts.Common;
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

    /// <summary>
    /// True when the state comes from a billing status the API just returned (or from a local
    /// sign-out), false when it was evaluated from the cached snapshot. Only an authoritative
    /// snapshot may remove the device result key: a stale cache is never evidence the add-on ended.
    /// </summary>
    public bool IsAuthoritative { get; init; }

    public static WinoIntelligenceEntitlementSnapshot SignedOut(DateTimeOffset now)
        => new(WinoIntelligenceEntitlementState.SignedOut, null, now) { IsAuthoritative = true };

    public static WinoIntelligenceEntitlementSnapshot Evaluate(
        Guid accountId,
        BillingStatusResultDto? billing,
        AiUsageStatusDto? usage,
        DateTimeOffset now,
        bool isFreshBilling)
        => EvaluateState(accountId, billing, usage, now, isFreshBilling) with { IsAuthoritative = isFreshBilling };

    private static WinoIntelligenceEntitlementSnapshot EvaluateState(
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

        var quotaExhausted = usage?.IsExhausted == true;
        return new(
            quotaExhausted ? WinoIntelligenceEntitlementState.QuotaExhausted : WinoIntelligenceEntitlementState.Active,
            accountId,
            now);
    }
}
