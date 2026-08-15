#nullable enable
using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;
using Wino.Mail.AI.Abstractions;

namespace Wino.Core.Domain.Models.Intelligence;

public sealed record LocalIntelligenceAccessSnapshot(Guid LocalAccountId, Guid WinoAccountId,
    bool HasAiPack, bool HasIntelligenceConsent, Guid? MailboxId, DateTimeOffset UpdatedAtUtc)
{
    public bool IsEligible => HasAiPack && HasIntelligenceConsent && MailboxId is not null;
}

public sealed record DailyBriefingAccount(MailAccount Account, Guid MailboxId);

/// <summary>
/// A language-neutral briefing fact joined with its separately stored localized headline.
/// </summary>
public sealed record DailyBriefingFact(Guid LocalAccountId, Guid MailUniqueId, string RemoteMessageId,
    string Subject, string Sender, DateTimeOffset OccurredAt, string Headline, long ArtifactRevision,
    BriefingFactCapabilityPayload Fact);

public sealed record DailyBriefingUnseenState(bool HasUnseenContent, DateTimeOffset? LastOpenedAtUtc);
