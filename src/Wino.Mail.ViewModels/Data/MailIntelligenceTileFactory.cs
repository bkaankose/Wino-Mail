using System;
using System.Collections.Generic;
using System.Globalization;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Controls.Core.IntelligenceTileBar;

namespace Wino.Mail.ViewModels.Data;

public static class MailIntelligenceTileFactory
{
    // Segoe Fluent Icons glyphs for the fact tiles. Shared so the settings checklist can show the
    // same icon next to the indicator the user is turning on or off.
    public const string DeadlineGlyph = "\uE787";           // Calendar
    public const string NeedsReplyGlyph = "\uE97A";         // Reply
    public const string PriorityGlyph = "\uE7BA";           // Warning
    public const string BriefingGlyph = "\uE946";           // Info

    /// <summary>Glyph used for a smart label that has no recognizable icon of its own.</summary>
    public const string FallbackSmartLabelGlyph = "\uE7C1"; // Flag

    public static IReadOnlyList<WinoIntelligenceTile> Create(MailIntelligenceMetadata metadata)
        => Create(metadata, excludedIndicatorIds: null);

    public static IReadOnlyList<WinoIntelligenceTile> Create(
        MailIntelligenceMetadata metadata,
        IReadOnlySet<string>? excludedIndicatorIds)
    {
        if (metadata is null)
        {
            return [];
        }

        var tiles = new List<WinoIntelligenceTile>();

        if (metadata.Deadline?.HasDeadline == true &&
            !IsExcluded(IntelligenceIndicatorIds.Deadline, excludedIndicatorIds))
        {
            var deadline = FormatDeadline(metadata.Deadline, CultureInfo.CurrentCulture);
            var accessibleText = $"{GetDeadlineKindText(metadata.Deadline.Kind)}: {deadline}";
            tiles.Add(new(WinoIntelligenceTileKind.Deadline, DeadlineGlyph, deadline, accessibleText));
        }

        if (metadata.NeedsReply?.Value == true &&
            !IsExcluded(IntelligenceIndicatorIds.NeedsReply, excludedIndicatorIds))
        {
            tiles.Add(new(WinoIntelligenceTileKind.NeedsReply, NeedsReplyGlyph, Translator.IntelligenceTile_NeedsReply, Translator.IntelligenceTile_NeedsReplyTooltip));
        }

        if ((metadata.Priority?.Level is MailPriority.High or MailPriority.Urgent) &&
            !IsExcluded(IntelligenceIndicatorIds.Priority, excludedIndicatorIds))
        {
            var priority = metadata.Priority.Level == MailPriority.Urgent
                ? Translator.IntelligenceTile_PriorityUrgent
                : Translator.IntelligenceTile_PriorityHigh;
            tiles.Add(new(WinoIntelligenceTileKind.Priority, PriorityGlyph, priority, priority, isWarning: true));
        }

        foreach (var smartLabel in metadata.SmartLabels)
        {
            if (IsExcluded(IntelligenceIndicatorIds.ForSmartLabel(smartLabel.Label), excludedIndicatorIds))
                continue;

            var label = GetSmartLabelText(smartLabel.Label);
            tiles.Add(new(
                WinoIntelligenceTileKind.SmartLabel,
                GetSmartLabelGlyph(smartLabel.Label),
                label,
                label,
                isWarning: smartLabel.Label == MailSmartLabel.Important));
        }

        if (metadata.BriefingFact is not null &&
            !IsExcluded(IntelligenceIndicatorIds.Briefing, excludedIndicatorIds))
        {
            var fact = metadata.BriefingFact;
            var accessibleText = string.Format(
                CultureInfo.CurrentCulture,
                Translator.IntelligenceTile_BriefingTooltipFormat,
                GetBriefingCategoryText(GetBriefingCategory(fact)),
                GetPriorityText(fact.Urgency),
                metadata.Headline);
            tiles.Add(new(WinoIntelligenceTileKind.BriefingFact, BriefingGlyph, string.Empty, accessibleText, true));
        }

        return tiles;
    }

    private static bool IsExcluded(string indicatorId, IReadOnlySet<string>? excludedIndicatorIds)
        => !IntelligenceVisibilityPolicy.IsVisible(
            excludedIndicatorIds,
            new IntelligenceIndicatorId(indicatorId));

    public static string FormatDeadline(DeadlineCapabilityPayload deadline, CultureInfo culture)
    {
        var dateText = deadline.Precision switch
        {
            DeadlinePrecision.DateTime when deadline.DueAtUtc.HasValue => deadline.DueAtUtc.Value.ToLocalTime().ToString("g", culture),
            DeadlinePrecision.Date when deadline.LocalDate.HasValue => deadline.LocalDate.Value.ToString("d", culture),
            DeadlinePrecision.Month when deadline.LocalDate.HasValue => deadline.LocalDate.Value.ToString("Y", culture),
            DeadlinePrecision.Range when deadline.LocalDate.HasValue && deadline.LocalDateEnd.HasValue =>
                string.Format(culture, Translator.IntelligenceTile_DateRangeFormat,
                    deadline.LocalDate.Value.ToString("d", culture),
                    deadline.LocalDateEnd.Value.ToString("d", culture)),
            _ when deadline.LocalDate.HasValue => deadline.LocalDate.Value.ToString("d", culture),
            _ when deadline.DueAtUtc.HasValue => deadline.DueAtUtc.Value.ToLocalTime().ToString("g", culture),
            _ => string.Empty
        };

        var action = GetDeadlineActionText(deadline.Action);
        if (string.IsNullOrEmpty(dateText))
        {
            return action;
        }

        return string.Format(culture, Translator.IntelligenceTile_DeadlineFormat, action, dateText);
    }

    private static string GetDeadlineActionText(DeadlineAction action) => action switch
    {
        DeadlineAction.Review => Translator.IntelligenceTile_DeadlineActionReview,
        DeadlineAction.Reply => Translator.IntelligenceTile_DeadlineActionReply,
        DeadlineAction.Pay => Translator.IntelligenceTile_DeadlineActionPay,
        DeadlineAction.Schedule => Translator.IntelligenceTile_DeadlineActionSchedule,
        DeadlineAction.Confirm => Translator.IntelligenceTile_DeadlineActionConfirm,
        DeadlineAction.Submit => Translator.IntelligenceTile_DeadlineActionSubmit,
        DeadlineAction.Cancel => Translator.IntelligenceTile_DeadlineActionCancel,
        DeadlineAction.FollowUp => Translator.IntelligenceTile_DeadlineActionFollowUp,
        _ => Translator.IntelligenceTile_Deadline
    };

    private static string GetDeadlineKindText(DeadlineKind kind) => kind switch
    {
        DeadlineKind.Reply => Translator.IntelligenceTile_DeadlineKindReply,
        DeadlineKind.Payment => Translator.IntelligenceTile_DeadlineKindPayment,
        DeadlineKind.Appointment => Translator.IntelligenceTile_DeadlineKindAppointment,
        DeadlineKind.Renewal => Translator.IntelligenceTile_DeadlineKindRenewal,
        DeadlineKind.Travel => Translator.IntelligenceTile_DeadlineKindTravel,
        DeadlineKind.Submission => Translator.IntelligenceTile_DeadlineKindSubmission,
        DeadlineKind.Cancellation => Translator.IntelligenceTile_DeadlineKindCancellation,
        _ => Translator.IntelligenceTile_DeadlineKindOther
    };

    // Segoe Fluent Icons glyphs, one per label so a tile is recognizable before its text is read.
    // A label with no glyph falls back to the neutral intelligence dot in the tile visuals.
    public static string GetSmartLabelGlyph(MailSmartLabel label) => label switch
    {
        MailSmartLabel.Important => "\uE734",     // Star
        MailSmartLabel.Action => "\uE945",        // Lightning bolt
        MailSmartLabel.Finance => "\uE8C7",       // Payment card
        MailSmartLabel.Travel => "\uE709",        // Airplane
        MailSmartLabel.Social => "\uE716",        // People
        MailSmartLabel.Newsletter => "\uE12A",    // Newspaper
        MailSmartLabel.Receipt => "\uE7BF",       // Shopping cart
        _ => string.Empty
    };

    private static string GetSmartLabelText(MailSmartLabel label) => label switch
    {
        MailSmartLabel.Important => Translator.IntelligenceTile_LabelImportant,
        MailSmartLabel.Action => Translator.IntelligenceTile_LabelAction,
        MailSmartLabel.Finance => Translator.IntelligenceTile_LabelFinance,
        MailSmartLabel.Travel => Translator.IntelligenceTile_LabelTravel,
        MailSmartLabel.Social => Translator.IntelligenceTile_LabelSocial,
        MailSmartLabel.Newsletter => Translator.IntelligenceTile_LabelNewsletter,
        MailSmartLabel.Receipt => Translator.IntelligenceTile_LabelReceipt,
        _ => label.ToString()
    };

    private static string GetBriefingCategoryText(BriefingFactCategory category) => category switch
    {
        BriefingFactCategory.Information => Translator.IntelligenceTile_BriefingCategoryInformation,
        BriefingFactCategory.ActionRequired => Translator.IntelligenceTile_BriefingCategoryActionRequired,
        BriefingFactCategory.Waiting => Translator.IntelligenceTile_BriefingCategoryWaiting,
        BriefingFactCategory.Security => Translator.IntelligenceTile_BriefingCategorySecurity,
        BriefingFactCategory.Finance => Translator.IntelligenceTile_BriefingCategoryFinance,
        BriefingFactCategory.Travel => Translator.IntelligenceTile_BriefingCategoryTravel,
        BriefingFactCategory.Personal => Translator.IntelligenceTile_BriefingCategoryPersonal,
        _ => Translator.IntelligenceTile_BriefingCategoryOther
    };

    private static BriefingFactCategory GetBriefingCategory(BriefingFactCapabilityPayload fact) => fact switch
    {
        SecurityFactPayload or AccountFactPayload => BriefingFactCategory.Security,
        FinanceFactPayload or PurchaseFactPayload or SubscriptionFactPayload => BriefingFactCategory.Finance,
        TravelFactPayload or ReservationFactPayload => BriefingFactCategory.Travel,
        ConversationFactPayload or SocialFactPayload => BriefingFactCategory.Personal,
        TaskFactPayload or ApprovalFactPayload or MeetingFactPayload => BriefingFactCategory.ActionRequired,
        _ when fact.Status is BriefingStatus.AwaitingMyReply or BriefingStatus.AwaitingOthers => BriefingFactCategory.Waiting,
        _ => BriefingFactCategory.Information,
    };

    private static string GetPriorityText(MailPriority priority) => priority switch
    {
        MailPriority.Low => Translator.IntelligenceTile_PriorityLow,
        MailPriority.High => Translator.IntelligenceTile_PriorityHigh,
        MailPriority.Urgent => Translator.IntelligenceTile_PriorityUrgent,
        _ => Translator.IntelligenceTile_PriorityNormal
    };
}
