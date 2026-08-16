#nullable enable
using System;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.ViewModels;

namespace Wino.Mail.WinUI.Controls;

public static class DailyBriefingPanelPresentation
{
    public static bool IsPriority(DailyBriefingFact fact)
        => fact.IsPriorityVisible && fact.Fact.Urgency is MailPriority.Urgent or MailPriority.High;

    public static string Headline(DailyBriefingFact fact)
        => string.IsNullOrWhiteSpace(fact.Headline) ? fact.Subject : fact.Headline;

    public static string Detail(DailyBriefingFact fact)
        => fact.IsNeedsReplyVisible || fact.Fact.Status is not BriefingStatus.AwaitingMyReply
            ? StatusText(fact.Fact.Status)
            : string.Empty;

    public static bool HasDetail(DailyBriefingFact fact) => !string.IsNullOrWhiteSpace(Detail(fact));
    public static Visibility DetailVisibility(DailyBriefingFact fact) => HasDetail(fact) ? Visibility.Visible : Visibility.Collapsed;

    public static string Source(DailyBriefingFact fact)
    {
        var hasSubject = !string.IsNullOrWhiteSpace(fact.Subject)
            && !fact.Headline.Contains(fact.Subject, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fact.Subject, fact.Headline, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(fact.Sender)) return hasSubject ? fact.Subject : string.Empty;
        return hasSubject ? $"{fact.Sender} · {fact.Subject}" : fact.Sender;
    }

    public static bool HasSource(DailyBriefingFact fact) => !string.IsNullOrWhiteSpace(Source(fact));
    public static Visibility SourceVisibility(DailyBriefingFact fact) => HasSource(fact) ? Visibility.Visible : Visibility.Collapsed;

    public static string When(DailyBriefingFact fact)
        => TimeZoneInfo.ConvertTime(fact.OccurredAt, TimeZoneInfo.Local).ToString("t", CultureInfo.CurrentCulture);

    public static string DueText(DailyBriefingFact fact)
        => fact.IsDeadlineVisible ? FormatTemporal(fact.Fact.TemporalReferences.FirstOrDefault()) : string.Empty;

    public static bool HasDue(DailyBriefingFact fact) => !string.IsNullOrWhiteSpace(DueText(fact));

    public static bool HasUrgency(DailyBriefingFact fact) => IsPriority(fact);
    public static Visibility UrgencyVisibility(DailyBriefingFact fact) => HasUrgency(fact) ? Visibility.Visible : Visibility.Collapsed;

    public static string UrgencyText(DailyBriefingFact fact) => fact.Fact.Urgency switch
    {
        MailPriority.Urgent => Translator.DailyBriefing_UrgencyUrgent,
        _ => Translator.DailyBriefing_UrgencyHigh,
    };

    public static BriefingFactCategory Category(DailyBriefingFact fact) => fact.Fact switch
    {
        SecurityFactPayload or AccountFactPayload => BriefingFactCategory.Security,
        FinanceFactPayload or PurchaseFactPayload or SubscriptionFactPayload => BriefingFactCategory.Finance,
        TravelFactPayload or ReservationFactPayload => BriefingFactCategory.Travel,
        ConversationFactPayload or SocialFactPayload => BriefingFactCategory.Personal,
        TaskFactPayload or ApprovalFactPayload or MeetingFactPayload => BriefingFactCategory.ActionRequired,
        _ when fact.Fact.Status is BriefingStatus.AwaitingMyReply or BriefingStatus.AwaitingOthers => BriefingFactCategory.Waiting,
        _ => BriefingFactCategory.Information,
    };

    public static DailyBriefingTone Tone(DailyBriefingFact fact) => Category(fact) switch
    {
        BriefingFactCategory.ActionRequired or BriefingFactCategory.Security => DailyBriefingTone.Critical,
        BriefingFactCategory.Finance or BriefingFactCategory.Waiting => DailyBriefingTone.Caution,
        BriefingFactCategory.Travel => DailyBriefingTone.Success,
        BriefingFactCategory.Personal => DailyBriefingTone.Attention,
        _ => DailyBriefingTone.Neutral,
    };

    public static string CategoryText(DailyBriefingFact fact) => Category(fact) switch
    {
        BriefingFactCategory.Information => Translator.IntelligenceTile_BriefingCategoryInformation,
        BriefingFactCategory.ActionRequired => Translator.IntelligenceTile_BriefingCategoryActionRequired,
        BriefingFactCategory.Waiting => Translator.IntelligenceTile_BriefingCategoryWaiting,
        BriefingFactCategory.Security => Translator.IntelligenceTile_BriefingCategorySecurity,
        BriefingFactCategory.Finance => Translator.IntelligenceTile_BriefingCategoryFinance,
        BriefingFactCategory.Travel => Translator.IntelligenceTile_BriefingCategoryTravel,
        BriefingFactCategory.Personal => Translator.IntelligenceTile_BriefingCategoryPersonal,
        _ => Translator.IntelligenceTile_BriefingCategoryOther,
    };

    public static string CategoryGlyph(DailyBriefingFact fact) => DailyBriefingIcons.Category(Category(fact));
    public static bool HasCategory(DailyBriefingFact fact) => !string.IsNullOrWhiteSpace(CategoryText(fact));
    public static Visibility CategoryVisibility(DailyBriefingFact fact) => HasCategory(fact) ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility DueVisibility(DailyBriefingFact fact) => HasDue(fact) ? Visibility.Visible : Visibility.Collapsed;

    public static string Headline(DailyBriefingItem item)
        => string.IsNullOrWhiteSpace(item.Fact.Headline) ? item.Fact.Subject : item.Fact.Headline;

    public static string Detail(DailyBriefingItem item)
        => item.IndicatorState.IsNeedsReplyVisible || item.Fact.Fact.Status is not BriefingStatus.AwaitingMyReply
            ? StatusText(item.Fact.Fact.Status)
            : string.Empty;

    public static bool HasDetail(DailyBriefingItem item) => !string.IsNullOrWhiteSpace(Detail(item));

    public static string Source(DailyBriefingItem item)
    {
        var headline = item.Fact.Headline;
        var subject = item.Fact.Subject;
        var hasSubject = !string.IsNullOrWhiteSpace(subject)
            && !headline.Contains(subject, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(subject, headline, StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(item.Fact.Sender)) return hasSubject ? subject : string.Empty;
        return hasSubject ? $"{item.Fact.Sender} · {subject}" : item.Fact.Sender;
    }

    public static bool HasSource(DailyBriefingItem item) => !string.IsNullOrWhiteSpace(Source(item));

    public static string When(DailyBriefingItem item)
        => TimeZoneInfo.ConvertTime(item.Fact.OccurredAt, TimeZoneInfo.Local).ToString("t", CultureInfo.CurrentCulture);

    public static string DueText(DailyBriefingItem item)
        => item.IndicatorState.IsDeadlineVisible
            ? FormatTemporal(item.Fact.Fact.TemporalReferences.FirstOrDefault())
            : string.Empty;

    public static bool HasDue(DailyBriefingItem item) => !string.IsNullOrWhiteSpace(DueText(item));

    public static BriefingFactCategory Category(DailyBriefingItem item) => item.Fact.Fact switch
    {
        SecurityFactPayload or AccountFactPayload => BriefingFactCategory.Security,
        FinanceFactPayload or PurchaseFactPayload or SubscriptionFactPayload => BriefingFactCategory.Finance,
        TravelFactPayload or ReservationFactPayload => BriefingFactCategory.Travel,
        ConversationFactPayload or SocialFactPayload => BriefingFactCategory.Personal,
        TaskFactPayload or ApprovalFactPayload or MeetingFactPayload => BriefingFactCategory.ActionRequired,
        _ when item.Fact.Fact.Status is BriefingStatus.AwaitingMyReply or BriefingStatus.AwaitingOthers => BriefingFactCategory.Waiting,
        _ => BriefingFactCategory.Information,
    };

    public static DailyBriefingTone Tone(DailyBriefingItem item) => Category(item) switch
    {
        BriefingFactCategory.ActionRequired or BriefingFactCategory.Security => DailyBriefingTone.Critical,
        BriefingFactCategory.Finance or BriefingFactCategory.Waiting => DailyBriefingTone.Caution,
        BriefingFactCategory.Travel => DailyBriefingTone.Success,
        BriefingFactCategory.Personal => DailyBriefingTone.Attention,
        _ => DailyBriefingTone.Neutral,
    };

    public static string CategoryText(DailyBriefingItem item) => Category(item) switch
    {
        BriefingFactCategory.Information => Translator.IntelligenceTile_BriefingCategoryInformation,
        BriefingFactCategory.ActionRequired => Translator.IntelligenceTile_BriefingCategoryActionRequired,
        BriefingFactCategory.Waiting => Translator.IntelligenceTile_BriefingCategoryWaiting,
        BriefingFactCategory.Security => Translator.IntelligenceTile_BriefingCategorySecurity,
        BriefingFactCategory.Finance => Translator.IntelligenceTile_BriefingCategoryFinance,
        BriefingFactCategory.Travel => Translator.IntelligenceTile_BriefingCategoryTravel,
        BriefingFactCategory.Personal => Translator.IntelligenceTile_BriefingCategoryPersonal,
        _ => Translator.IntelligenceTile_BriefingCategoryOther,
    };

    public static string CategoryGlyph(DailyBriefingItem item) => DailyBriefingIcons.Category(Category(item));

    public static bool HasCategory(DailyBriefingItem item) => !string.IsNullOrWhiteSpace(CategoryText(item));

    public static string UrgencyText(DailyBriefingItem item) => item.Fact.Fact.Urgency switch
    {
        MailPriority.Urgent => Translator.DailyBriefing_UrgencyUrgent,
        _ => Translator.DailyBriefing_UrgencyHigh,
    };

    public static bool HasUrgency(DailyBriefingItem item) => item.IsPriority;

    public static DailyBriefingActionPresentation Action(DailyBriefingItem item)
        => DailyBriefingActionPresentationFactory.Create(
            item.Fact.Fact.PrimaryAction,
            canAddToCalendar: item.CalendarArgs is not null,
            hasVerificationCode: !string.IsNullOrWhiteSpace(item.VerificationCode),
            allowReplyAction: item.IndicatorState.IsNeedsReplyVisible);

    public static bool ShowOpenAction(DailyBriefingItem item)
        => item.CanOpen && Action(item).Execution != DailyBriefingActionExecution.OpenSource;

    private static string FormatTemporal(TemporalPayload? temporal) => temporal switch
    {
        DeadlineTemporalPayload x => FormatSingle(Translator.DailyBriefing_TemporalDue, x.Due),
        EventTemporalPayload x => FormatRange(Translator.DailyBriefing_TemporalEvent, x.Start, x.End),
        DateRangeTemporalPayload x => FormatRange(Translator.DailyBriefing_TemporalRange, x.Start, x.End),
        AvailabilityWindowTemporalPayload x => FormatRange(Translator.DailyBriefing_TemporalAvailable, x.Opens, x.Closes),
        CoveragePeriodTemporalPayload x => FormatRange(Translator.DailyBriefing_TemporalCoverage, x.Start, x.End),
        ExpectedTemporalPayload x => FormatSingle(Translator.DailyBriefing_TemporalExpected, x.ExpectedAt),
        ExpirationTemporalPayload x => FormatSingle(Translator.DailyBriefing_TemporalExpires, x.ExpiresAt),
        RenewalTemporalPayload x => FormatSingle(Translator.DailyBriefing_TemporalRenews, x.RenewsAt),
        TravelTemporalPayload x => FormatRange(Translator.DailyBriefing_TemporalTravel, x.Departure, x.Arrival),
        _ => string.Empty,
    };

    private static string FormatSingle(string label, TemporalPointPayload point)
    {
        var value = FormatPoint(point);
        return string.IsNullOrEmpty(value) ? string.Empty : $"{label} {value}";
    }

    private static string FormatRange(string label, TemporalPointPayload start, TemporalPointPayload? end)
    {
        var startText = FormatPoint(start);
        if (string.IsNullOrEmpty(startText)) return string.Empty;
        var endText = end is null ? string.Empty : FormatPoint(end);
        return string.IsNullOrEmpty(endText) ? $"{label} {startText}" : $"{label} {startText} – {endText}";
    }

    private static string FormatPoint(TemporalPointPayload point)
    {
        if (point.InstantUtc is { } instant)
            return TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Local).ToString("g", CultureInfo.CurrentCulture);
        if (point.LocalDate is { } date && point.LocalTime is { } time)
        {
            var zone = string.IsNullOrWhiteSpace(point.TimeZoneId) ? string.Empty : $" {point.TimeZoneId}";
            return $"{date.ToString("d", CultureInfo.CurrentCulture)} {time.ToString("t", CultureInfo.CurrentCulture)}{zone}";
        }
        if (point.LocalDate is { } localDate) return localDate.ToString("d", CultureInfo.CurrentCulture);
        return point.LocalTime?.ToString("t", CultureInfo.CurrentCulture) ?? string.Empty;
    }

    private static string StatusText(BriefingStatus status) => status switch
    {
        BriefingStatus.ActionRequired => Translator.DailyBriefing_StatusActionRequired,
        BriefingStatus.AwaitingMyReply => Translator.DailyBriefing_StatusAwaitingMyReply,
        BriefingStatus.AwaitingOthers => Translator.DailyBriefing_StatusAwaitingOthers,
        BriefingStatus.Scheduled => Translator.DailyBriefing_StatusScheduled,
        BriefingStatus.InProgress => Translator.DailyBriefing_StatusInProgress,
        BriefingStatus.Completed => Translator.DailyBriefing_StatusCompleted,
        BriefingStatus.Updated => Translator.DailyBriefing_StatusUpdated,
        BriefingStatus.Cancelled => Translator.DailyBriefing_StatusCancelled,
        BriefingStatus.Expired => Translator.DailyBriefing_StatusExpired,
        _ => string.Empty,
    };
}
