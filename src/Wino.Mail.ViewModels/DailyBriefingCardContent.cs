#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Everything one Daily briefing card shows, already localised (Windows DailyBriefingPanelPresentation): the headline
/// (the Enrichment headline, or the subject before Enrichment ran), the arrival time, the "new" state, the summary, the
/// urgency chip, the label chips that fit beside the action, the primary action and the ignore toggle. The Mac, iOS and
/// Android panels draw it.
/// </summary>
public sealed record DailyBriefingCardContent(
    string Headline,
    string Time,
    bool IsNew,
    string? Summary,
    string? Urgency,
    IReadOnlyList<string> Labels,
    string ActionGlyph,
    string ActionText,
    string IgnoreGlyph,
    string IgnoreText,
    bool CanToggleIgnore)
{
    /// <summary>How many chips fit beside the longest action wording; a priority chip takes one of the places.</summary>
    public const int MaxChipsPerRow = 2;

    public static DailyBriefingCardContent From(DailyBriefingItem item, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var fact = item.Fact;
        var headline = string.IsNullOrWhiteSpace(fact.Headline) ? fact.Subject : fact.Headline;
        var time = TimeZoneInfo.ConvertTime(fact.ReceivedAt, zone ?? TimeZoneInfo.Local).ToString("t", culture ?? CultureInfo.CurrentCulture);
        string? urgency = item.IsPriority
            ? string.Equals(fact.Priority, "urgent", StringComparison.OrdinalIgnoreCase) ? Translator.DailyBriefing_UrgencyUrgent : Translator.DailyBriefing_UrgencyHigh
            : null;
        var room = urgency is null ? MaxChipsPerRow : MaxChipsPerRow - 1;
        List<string> labels = item.LabelChips.Take(room).Select(chip => chip.Text).ToList();
        return new DailyBriefingCardContent(headline, time, item.IsNew, item.HasSummary ? item.Summary : null, urgency, labels,
            item.PrimaryActionGlyph, item.PrimaryActionText, item.IgnoreActionGlyph, item.IgnoreActionText, item.CanToggleIgnore);
    }
}

/// <summary>Whether the shell offers the Daily briefing and whether it has unseen content.</summary>
public readonly record struct DailyBriefingShellState(bool IsAvailable, bool HasUnseenItems)
{
    public static DailyBriefingShellState Unavailable => new(false, false);
}

/// <summary>
/// The shell rule for the Daily briefing entry (Windows ShellWindow title-bar button, Mac and iOS presenters): it is
/// offered only while the signed-in Wino account can use Intelligence surfaces, and it is unseen only when at least one
/// account is eligible and the local store has unseen content.
/// </summary>
public static class DailyBriefingAvailability
{
    public static async Task<DailyBriefingShellState> ReadAsync(
        IWinoAccountIntelligenceSnapshotService entitlements,
        ILocalIntelligenceService localIntelligence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entitlements);
        ArgumentNullException.ThrowIfNull(localIntelligence);
        var entitlement = await entitlements.GetEntitlementAsync(cancellationToken).ConfigureAwait(false);
        if (!entitlement.CanAccessSurfaces) return DailyBriefingShellState.Unavailable;
        var eligible = await localIntelligence.GetEligibleAccountsAsync(cancellationToken).ConfigureAwait(false);
        if (eligible.Count == 0) return new DailyBriefingShellState(true, false);
        var unseen = await localIntelligence.GetUnseenStateAsync(cancellationToken).ConfigureAwait(false);
        return new DailyBriefingShellState(true, unseen.HasUnseenContent);
    }
}
