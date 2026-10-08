#nullable enable
using System;
using System.Text.Json.Serialization;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Core.Domain.Models.Intelligence;

/// <summary>
/// Persisted account-scoped server metadata, so intelligence surfaces render without
/// waiting for the network.
/// The per-mailbox index status and head are gone: intelligence results are device-local
/// now, so there is no server-side mailbox state left to cache.
/// </summary>
public sealed record WinoAccountIntelligenceSnapshot(
    Guid WinoAccountId,
    BillingStatusResultDto? Billing,
    IntelligenceConsentDto? Consent,
    AiUsageStatusDto? Usage,
    DateTimeOffset? BillingUpdatedAtUtc,
    DateTimeOffset? ConsentUpdatedAtUtc,
    DateTimeOffset? UsageUpdatedAtUtc,
    DateTimeOffset? LastSuccessfulRefreshUtc)
{
    [JsonIgnore]
    public WinoAccountSession? Session { get; init; }

    public static WinoAccountIntelligenceSnapshot Empty(Guid accountId)
        => new(accountId, null, null, null, null, null, null, null);

    public bool HasData => Billing is not null || Consent is not null || Usage is not null;
}

public sealed record WinoAccountIntelligenceRefreshResult(
    WinoAccountIntelligenceSnapshot Snapshot,
    bool AnySectionUpdated,
    string? Error)
{
    public bool BillingRefreshed { get; init; }
}
