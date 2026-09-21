#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Mail.ViewModels;

/// <summary>
/// One briefing card. It shows only what Classification and Summarization produce: sender and date, smart
/// labels, priority, a headline, a one-line summary, and Open.
/// There is no action button, status or due date, because the decision model cannot
/// produce those reliably.
/// </summary>
public sealed partial class DailyBriefingItem : ObservableObject
{
    public DailyBriefingItem(DailyBriefingFact fact, DailyBriefingAccount account)
    {
        Fact = fact;
        Account = account;
        IsIgnored = fact.IsIgnored;
    }

    public DailyBriefingFact Fact { get; }

    public DailyBriefingAccount Account { get; }

    public Guid LocalAccountId => Fact.LocalAccountId;

    public Guid MailUniqueId => Fact.MailUniqueId;

    public string RemoteMessageId => Fact.RemoteMessageId;

    /// <summary>The hash the card was built from. Ignoring is keyed on it.</summary>
    public string ContentHash => Fact.ContentHash;

    public string Subject => Fact.Subject;

    public string SenderName => string.IsNullOrWhiteSpace(Fact.SenderName) ? Fact.SenderAddress : Fact.SenderName;

    public string SenderAddress => Fact.SenderAddress;

    public DateTimeOffset ReceivedAt => Fact.ReceivedAt;

    public string Headline => Fact.IsHeadlineVisible ? Fact.Headline : string.Empty;

    public string Summary => Fact.Summary;

    public bool HasHeadline => !string.IsNullOrWhiteSpace(Headline);

    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    public IReadOnlyList<string> Labels => Fact.VisibleLabels;

    /// <summary>
    /// Label chips as a typed model. Binding to plain strings would force a cast inside
    /// the template, which the XAML compiler handles poorly.
    /// </summary>
    public IReadOnlyList<BriefingLabelChip> LabelChips =>
        (BriefingLabelChip[])[.. Labels.Select(BriefingLabelChip.Create)];

    public bool HasLabels => Labels.Count > 0;

    public string Priority => Fact.Priority;

    public bool IsPriority => Fact.IsPriorityVisible &&
        (string.Equals(Priority, "urgent", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(Priority, "high", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Priority is the only signal left that carries urgency, so the card's tone follows
    /// it directly rather than being inferred from a fact category.
    /// </summary>
    public DailyBriefingTone Tone => Priority.ToLowerInvariant() switch
    {
        "urgent" => DailyBriefingTone.Critical,
        "high" => DailyBriefingTone.Caution,
        "low" => DailyBriefingTone.Neutral,
        _ => DailyBriefingTone.Attention,
    };

    public bool CanOpen => MailUniqueId != Guid.Empty;

    [ObservableProperty]
    public partial bool IsIgnored { get; set; }

    [ObservableProperty]
    public partial bool IsIgnorePending { get; set; }

    public bool CanToggleIgnore => !IsIgnorePending;

    public string IgnoreActionText => IsIgnored ? Translator.DailyBriefing_ActionUnignore : Translator.DailyBriefing_ActionIgnore;

    public string IgnoreActionAutomationId => IsIgnored ? "DailyBriefingUnignoreButton" : "DailyBriefingIgnoreButton";

    public string IgnoreActionGlyph => IsIgnored ? DailyBriefingIcons.Show : DailyBriefingIcons.Hide;

    public string OpenActionAutomationId => "DailyBriefingOpenButton";

    /// <summary>Set by the panel when the card arrived after the briefing was last viewed.</summary>
    [ObservableProperty]
    public partial bool IsNew { get; set; }

    partial void OnIsIgnoredChanged(bool value)
    {
        OnPropertyChanged(nameof(IgnoreActionText));
        OnPropertyChanged(nameof(IgnoreActionAutomationId));
        OnPropertyChanged(nameof(IgnoreActionGlyph));
    }

    partial void OnIsIgnorePendingChanged(bool value) => OnPropertyChanged(nameof(CanToggleIgnore));
}

/// <summary>One smart-label chip on a briefing card.</summary>
public sealed record BriefingLabelChip(string Text, string Glyph)
{
    public static BriefingLabelChip Create(string label)
        => new(Data.MailIntelligenceTileFactory.GetSmartLabelText(label), DailyBriefingIcons.Label(label));
}
