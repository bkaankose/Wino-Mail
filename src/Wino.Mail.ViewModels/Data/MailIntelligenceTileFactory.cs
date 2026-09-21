using System;
using System.Collections.Generic;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Controls.Core.IntelligenceTileBar;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// Builds the indicator tiles shown on a mail row.
/// Only priority and smart labels remain: deadlines, needs-reply and the briefing-fact
/// category all came from inferences the decision model cannot make.
/// </summary>
public static class MailIntelligenceTileFactory
{
    public const string PriorityGlyph = "";           // Warning

    /// <summary>Glyph used for a smart label that has no recognizable icon of its own.</summary>
    public const string FallbackSmartLabelGlyph = ""; // Flag

    public static IReadOnlyList<WinoIntelligenceTile> Create(MailIntelligenceMetadata metadata)
        => Create(metadata, excludedIndicatorIds: null);

    public static IReadOnlyList<WinoIntelligenceTile> Create(
        MailIntelligenceMetadata metadata,
        IReadOnlySet<string> excludedIndicatorIds)
    {
        if (metadata is null)
        {
            return [];
        }

        var tiles = new List<WinoIntelligenceTile>();

        if (metadata.IsHighPriority && !IsExcluded(IntelligenceIndicatorId.Priority, excludedIndicatorIds))
        {
            var isUrgent = string.Equals(metadata.Priority, "urgent", StringComparison.OrdinalIgnoreCase);
            var priority = isUrgent
                ? Translator.IntelligenceTile_PriorityUrgent
                : Translator.IntelligenceTile_PriorityHigh;
            tiles.Add(new(WinoIntelligenceTileKind.Priority, PriorityGlyph, priority, priority, isWarning: true));
        }

        foreach (var name in metadata.Labels)
        {
            if (!Enum.TryParse<MailSmartLabel>(name, ignoreCase: true, out var label) || !Enum.IsDefined(label))
            {
                continue;
            }

            if (IsExcluded(IntelligenceIndicatorId.ForSmartLabel(label).Value, excludedIndicatorIds))
            {
                continue;
            }

            var text = GetSmartLabelText(label);
            tiles.Add(new(
                WinoIntelligenceTileKind.SmartLabel,
                GetSmartLabelGlyph(label),
                text,
                text,
                isWarning: label == MailSmartLabel.Important));
        }

        return tiles;
    }

    private static bool IsExcluded(string indicatorId, IReadOnlySet<string> excludedIndicatorIds)
        => !IntelligenceVisibilityPolicy.IsVisible(
            excludedIndicatorIds,
            new IntelligenceIndicatorId(indicatorId));

    // Segoe Fluent Icons glyphs, one per label so a tile is recognizable before its text is read.
    public static string GetSmartLabelGlyph(MailSmartLabel label) => label switch
    {
        MailSmartLabel.Important => "",     // Star
        MailSmartLabel.ActionRequired => "",        // Lightning bolt
        MailSmartLabel.Finance => "",       // Payment card
        MailSmartLabel.Travel => "",        // Airplane
        MailSmartLabel.Social => "",        // People
        MailSmartLabel.Newsletter => "",    // Newspaper
        MailSmartLabel.Receipt => "",       // Shopping cart
        _ => FallbackSmartLabelGlyph
    };

    public static string GetSmartLabelText(MailSmartLabel label) => label switch
    {
        MailSmartLabel.Important => Translator.IntelligenceTile_LabelImportant,
        MailSmartLabel.ActionRequired => Translator.IntelligenceTile_LabelActionRequired,
        MailSmartLabel.Finance => Translator.IntelligenceTile_LabelFinance,
        MailSmartLabel.Travel => Translator.IntelligenceTile_LabelTravel,
        MailSmartLabel.Social => Translator.IntelligenceTile_LabelSocial,
        MailSmartLabel.Newsletter => Translator.IntelligenceTile_LabelNewsletter,
        MailSmartLabel.Receipt => Translator.IntelligenceTile_LabelReceipt,
        _ => label.ToString()
    };

    /// <summary>Localized label text for a label name as stored locally, lowercase.</summary>
    public static string GetSmartLabelText(string label)
        => Enum.TryParse<MailSmartLabel>(label, ignoreCase: true, out var parsed)
            ? GetSmartLabelText(parsed)
            : label;

    public static string GetPriorityText(string priority) => priority.ToLowerInvariant() switch
    {
        "low" => Translator.IntelligenceTile_PriorityLow,
        "high" => Translator.IntelligenceTile_PriorityHigh,
        "urgent" => Translator.IntelligenceTile_PriorityUrgent,
        _ => Translator.IntelligenceTile_PriorityNormal
    };
}
