using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Controls.Core.IntelligenceTileBar;

namespace Wino.Mail.ViewModels.Data;

/// <summary>Stable identifiers used by local per-mailbox intelligence presentation preferences.</summary>
public static class IntelligenceIndicatorIds
{
    public const string Deadline = IntelligenceIndicatorId.FactDeadline;
    public const string NeedsReply = IntelligenceIndicatorId.FactNeedsReply;
    public const string Priority = IntelligenceIndicatorId.FactPriority;
    public const string Briefing = IntelligenceIndicatorId.FactBriefing;

    public static string ForSmartLabel(MailSmartLabel label) => IntelligenceIndicatorId.ForSmartLabel(label).Value;

    public static string ForTile(WinoIntelligenceTile tile) => tile.Kind switch
    {
        WinoIntelligenceTileKind.Deadline => Deadline,
        WinoIntelligenceTileKind.NeedsReply => NeedsReply,
        WinoIntelligenceTileKind.Priority => Priority,
        WinoIntelligenceTileKind.BriefingFact => Briefing,
        WinoIntelligenceTileKind.SmartLabel => string.Empty,
        _ => string.Empty,
    };
}

/// <summary>One local intelligence indicator shown in the mailbox settings checklist.</summary>
public sealed partial class IntelligenceIndicatorSettingsItem : ObservableObject
{
    public IntelligenceIndicatorSettingsItem(string identifier, string displayName, string automationId,
        string glyph, bool isVisible)
    {
        Identifier = identifier;
        DisplayName = displayName;
        AutomationId = automationId;
        Glyph = glyph;
        IsVisible = isVisible;
    }

    /// <summary>Gets the stable, mailbox-independent indicator identifier.</summary>
    public string Identifier { get; }

    /// <summary>Gets the translated name rendered next to the visibility toggle.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the Segoe Fluent Icons glyph this indicator uses on the mail intelligence header.</summary>
    public string Glyph { get; }

    /// <summary>Gets the stable UI Automation identifier for this indicator toggle.</summary>
    public string AutomationId { get; }

    /// <summary>Gets the stable UI Automation identifier for this indicator's settings card.</summary>
    public string CardAutomationId => $"{AutomationId}Card";

    [ObservableProperty]
    public partial bool IsVisible { get; set; }
}

/// <summary>Builds the ordered settings checklist and translates contract smart labels.</summary>
public static class IntelligenceIndicatorSettingsCatalog
{
    // Each entry carries the same glyph the header tile uses, so the checklist row reads as the
    // indicator it turns on or off.
    private static readonly (string Identifier, string DisplayName, string Glyph)[] FactIndicators =
    [
        (IntelligenceIndicatorIds.Deadline, Translator.IntelligenceTile_Deadline, MailIntelligenceTileFactory.DeadlineGlyph),
        (IntelligenceIndicatorIds.NeedsReply, Translator.IntelligenceTile_NeedsReply, MailIntelligenceTileFactory.NeedsReplyGlyph),
        (IntelligenceIndicatorIds.Priority, Translator.IntelligenceSettings_Priority, MailIntelligenceTileFactory.PriorityGlyph),
        (IntelligenceIndicatorIds.Briefing, Translator.IntelligenceSettings_BriefingFact, MailIntelligenceTileFactory.BriefingGlyph),
    ];

    public static ObservableCollection<IntelligenceIndicatorSettingsItem> Create(
        IReadOnlySet<string>? excludedIndicatorIds = null)
    {
        var items = new ObservableCollection<IntelligenceIndicatorSettingsItem>();

        foreach (var indicator in FactIndicators)
        {
            items.Add(CreateItem(indicator.Identifier, indicator.DisplayName, indicator.Glyph, excludedIndicatorIds));
        }

        foreach (var label in Enum.GetValues<MailSmartLabel>())
        {
            var identifier = IntelligenceIndicatorIds.ForSmartLabel(label);
            items.Add(CreateItem(identifier, TranslateSmartLabel(label), GetSmartLabelGlyph(label), excludedIndicatorIds));
        }

        return items;
    }

    /// <summary>Gets the header glyph for a smart label, or a neutral one when the label has none.</summary>
    public static string GetSmartLabelGlyph(MailSmartLabel label)
    {
        var glyph = MailIntelligenceTileFactory.GetSmartLabelGlyph(label);
        return string.IsNullOrEmpty(glyph) ? MailIntelligenceTileFactory.FallbackSmartLabelGlyph : glyph;
    }

    public static string TranslateSmartLabel(MailSmartLabel label)
    {
        var translated = Translator.GetTranslatedString($"IntelligenceTile_Label{label}");
        return string.IsNullOrWhiteSpace(translated) || translated.StartsWith("IntelligenceTile_Label", StringComparison.Ordinal)
            ? label.ToString()
            : translated;
    }

    public static string ToAutomationId(string identifier)
    {
        var suffix = identifier
            .Replace(':', '_')
            .Replace('-', '_');
        return $"WinoIntelligenceIndicatorToggle_{suffix}";
    }

    private static IntelligenceIndicatorSettingsItem CreateItem(string identifier, string displayName,
        string glyph, IReadOnlySet<string>? excludedIndicatorIds)
        => new(identifier, displayName, ToAutomationId(identifier), glyph,
            excludedIndicatorIds?.Contains(identifier) != true);
}
