#nullable enable
using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Intelligence;

public sealed record LocalIntelligenceAccessSnapshot(
    Guid LocalAccountId,
    Guid WinoAccountId,
    bool HasAiPack,
    bool HasIntelligenceConsent,
    Guid? MailboxId,
    DateTimeOffset UpdatedAtUtc)
{
    public bool IsEligible => HasAiPack && HasIntelligenceConsent && MailboxId is not null;
}

public sealed record DailyBriefingAccount(MailAccount Account, Guid? MailboxId = null);

/// <summary>
/// Which indicators the user wants to see for one account. Filtering happens at display
/// time so a settings change never rewrites a stored artifact.
/// </summary>
public sealed record DailyBriefingIndicatorState(
    bool IsPriorityVisible,
    bool IsHeadlineVisible,
    IReadOnlySet<string> EnabledLabels)
{
    public static DailyBriefingIndicatorState AllVisible(IReadOnlySet<string>? enabledLabels = null)
        => new(true, true, enabledLabels ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// One briefing entry: a message Classification included, described only in terms Classification and Enrichment can
/// actually support.
/// </summary>
public sealed record DailyBriefingFact(
    Guid LocalAccountId,
    Guid MailUniqueId,
    string RemoteMessageId,
    string ContentHash,
    string Subject,
    string SenderName,
    string SenderAddress,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<string> Labels,
    string Priority,
    string Action,
    string Headline,
    string Summary,
    DateTime FirstImportedUtc,
    DailyBriefingIndicatorState? IndicatorState = null,
    bool IsIgnored = false)
{
    public IReadOnlyList<string> VisibleLabels
    {
        get
        {
            if (IndicatorState is null || IndicatorState.EnabledLabels.Count == 0)
            {
                return Labels;
            }

            var visible = new List<string>(Labels.Count);
            foreach (var label in Labels)
            {
                if (IndicatorState.EnabledLabels.Contains(label))
                {
                    visible.Add(label);
                }
            }

            return visible;
        }
    }

    public bool IsPriorityVisible => IndicatorState?.IsPriorityVisible ?? true;

    public bool IsHeadlineVisible => IndicatorState?.IsHeadlineVisible ?? true;

    /// <summary>
    /// New since the briefing was last viewed. Keyed on when the artifact first arrived
    /// locally, which replaces the artifact revision the old change feed carried.
    /// </summary>
    public bool IsNewSince(DateTime? lastViewedUtc)
        => lastViewedUtc is null || FirstImportedUtc > lastViewedUtc.Value;
}

/// <summary>Briefing entries for one received day.</summary>
public sealed record DailyBriefingDay(DateOnly LocalDate, IReadOnlyList<DailyBriefingFact> Facts);

public sealed record DailyBriefingFactsResult(
    IReadOnlyList<DailyBriefingDay> Days,
    int TotalCount,
    int IgnoredCount)
{
    public static DailyBriefingFactsResult Empty { get; } = new([], 0, 0);
}

public sealed record DailyBriefingUnseenState(bool HasUnseenContent, DateTime? LastViewedUtc);
