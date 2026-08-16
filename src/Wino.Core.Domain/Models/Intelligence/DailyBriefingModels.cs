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

public sealed record DailyBriefingAccount(MailAccount Account, Guid? MailboxId = null);

/// <summary>
/// Presentation state calculated from one mailbox's local intelligence settings.
/// Daily Briefing keeps source labels separate from the included chips so a later
/// settings change never mutates the indexed artifact.
/// </summary>
public sealed record DailyBriefingIndicatorState(
    bool IsDeadlineVisible,
    bool IsNeedsReplyVisible,
    bool IsPriorityVisible,
    bool IsBriefingVisible,
    IReadOnlyList<SmartLabelScore> IncludedSmartLabels)
{
    public static DailyBriefingIndicatorState AllVisible(IReadOnlyList<SmartLabelScore>? smartLabels = null)
        => new(true, true, true, true, smartLabels ?? []);
}

/// <summary>
/// A language-neutral briefing fact joined with its separately stored localized headline.
/// </summary>
public sealed record DailyBriefingFact(Guid LocalAccountId, Guid MailUniqueId, string RemoteMessageId,
    string Subject, string Sender, DateTimeOffset OccurredAt, string Headline, long ArtifactRevision,
    BriefingFactCapabilityPayload Fact,
    IReadOnlyList<SmartLabelScore>? SourceSmartLabels = null,
    DailyBriefingIndicatorState? IndicatorState = null,
    bool IsIgnored = false)
{
    /// <summary>Raw smart-label artifact values carried alongside the source fact.</summary>
    public IReadOnlyList<SmartLabelScore> SmartLabels => SourceSmartLabels ?? [];

    /// <summary>Smart-label chips remaining after local mailbox filtering.</summary>
    public IReadOnlyList<SmartLabelScore> IncludedSmartLabels
        => IndicatorState?.IncludedSmartLabels ?? SmartLabels;

    public bool IsDeadlineVisible => IndicatorState?.IsDeadlineVisible ?? true;
    public bool IsNeedsReplyVisible => IndicatorState?.IsNeedsReplyVisible ?? true;
    public bool IsPriorityVisible => IndicatorState?.IsPriorityVisible ?? true;
    public bool IsBriefingVisible => IndicatorState?.IsBriefingVisible ?? true;
}

public sealed record DailyBriefingFactsResult(
    IReadOnlyList<DailyBriefingFact> Facts,
    bool HasIgnoredFacts);

public sealed record DailyBriefingIgnoreEntry(
    Guid LocalAccountId,
    Guid BriefingId,
    long IgnoredArtifactRevision,
    DateTimeOffset IgnoredAtUtc);

public sealed record DailyBriefingUnseenState(bool HasUnseenContent, DateTimeOffset? LastOpenedAtUtc);
