#nullable enable
using System;
using System.Collections.Generic;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Contracts.Intelligence;
using Wino.Mail.Contracts.SemanticIndex;

namespace Wino.Core.Domain.Models.Intelligence;

/// <summary>Persisted, account-scoped server metadata used to render Intelligence surfaces without waiting for the network.</summary>
public sealed record WinoAccountIntelligenceSnapshot(
    Guid WinoAccountId,
    BillingStatusResultDto? Billing,
    IntelligenceConsentDto? Consent,
    AiUsageStatusDto? Usage,
    IReadOnlyList<SemanticMailboxDto> Mailboxes,
    IReadOnlyDictionary<Guid, IntelligenceMailboxStatusDto> MailboxStatuses,
    DateTimeOffset? BillingUpdatedAtUtc,
    DateTimeOffset? ConsentUpdatedAtUtc,
    DateTimeOffset? UsageUpdatedAtUtc,
    DateTimeOffset? MailboxesUpdatedAtUtc,
    DateTimeOffset? StatusesUpdatedAtUtc,
    DateTimeOffset? LastSuccessfulRefreshUtc)
{
    public IReadOnlyDictionary<Guid, MailboxIntelligenceHeadDto> MailboxHeads { get; init; }
        = new Dictionary<Guid, MailboxIntelligenceHeadDto>();

    public DateTimeOffset? HeadsUpdatedAtUtc { get; init; }

    public static WinoAccountIntelligenceSnapshot Empty(Guid accountId) => new(
        accountId, null, null, null, [], new Dictionary<Guid, IntelligenceMailboxStatusDto>(),
        null, null, null, null, null, null);

    public bool HasData => Billing is not null || Consent is not null || Usage is not null || Mailboxes.Count > 0;
}

public sealed record WinoAccountIntelligenceRefreshResult(
    WinoAccountIntelligenceSnapshot Snapshot,
    bool AnySectionUpdated,
    string? Error);
