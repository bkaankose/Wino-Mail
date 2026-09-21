#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Microsoft.UI.Xaml;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.WinUI.Controls;

/// <summary>
/// Presentation helpers for the briefing panel.
/// A card shows sender and date, labels, priority, the Summarization headline and its one-line
/// summary. There is nothing here for due dates, actions or statuses, because the
/// decision model does not produce them.
/// </summary>
public static class DailyBriefingPanelPresentation
{
    public static bool IsPriority(DailyBriefingFact fact)
        => fact.IsPriorityVisible &&
           (string.Equals(fact.Priority, "urgent", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fact.Priority, "high", StringComparison.OrdinalIgnoreCase));

    /// <summary>The Summarization headline, falling back to the subject when Summarization has not run yet.</summary>
    public static string Headline(DailyBriefingFact fact)
        => string.IsNullOrWhiteSpace(fact.Headline) ? fact.Subject : fact.Headline;

    public static string Summary(DailyBriefingFact fact) => fact.Summary;

    public static bool HasSummary(DailyBriefingFact fact) => !string.IsNullOrWhiteSpace(fact.Summary);

    public static Visibility SummaryVisibility(DailyBriefingFact fact)
        => HasSummary(fact) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Time the message was received. The only date the briefing states.</summary>
    public static string When(DailyBriefingFact fact)
        => TimeZoneInfo.ConvertTime(fact.ReceivedAt, TimeZoneInfo.Local).ToString("t", CultureInfo.CurrentCulture);

    public static bool HasUrgency(DailyBriefingFact fact) => IsPriority(fact);

    public static Visibility UrgencyVisibility(DailyBriefingFact fact)
        => HasUrgency(fact) ? Visibility.Visible : Visibility.Collapsed;

    public static string UrgencyText(DailyBriefingFact fact)
        => string.Equals(fact.Priority, "urgent", StringComparison.OrdinalIgnoreCase)
            ? Translator.DailyBriefing_UrgencyUrgent
            : Translator.DailyBriefing_UrgencyHigh;

    public static bool HasLabels(DailyBriefingFact fact) => fact.VisibleLabels.Count > 0;

    public static Visibility LabelsVisibility(DailyBriefingFact fact)
        => HasLabels(fact) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Localized text for one smart-label chip.</summary>
    public static string LabelText(string label) => MailIntelligenceTileFactory.GetSmartLabelText(label);

    public static string LabelGlyph(string label) => DailyBriefingIcons.Label(label);

    public static Visibility NewBadgeVisibility(bool isNew) => isNew ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The chips that fit on the card's last line beside its command. A priority chip takes one of
    /// the two places, so the labels give way to it rather than running under the button.
    /// </summary>
    public static IReadOnlyList<BriefingLabelChip> TopLabelChips(IReadOnlyList<BriefingLabelChip> chips, DailyBriefingFact fact)
    {
        var room = HasUrgency(fact) ? MaxChipsPerRow - 1 : MaxChipsPerRow;
        return chips.Count <= room ? chips : [.. chips.Take(room)];
    }

    /// <summary>How many chips fit beside the longest action wording at the panel's width.</summary>
    private const int MaxChipsPerRow = 2;

    public static DailyBriefingTone Tone(DailyBriefingFact fact) => fact.Priority.ToLowerInvariant() switch
    {
        "urgent" => DailyBriefingTone.Critical,
        "high" => DailyBriefingTone.Caution,
        "low" => DailyBriefingTone.Neutral,
        _ => DailyBriefingTone.Attention,
    };
}
