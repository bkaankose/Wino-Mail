#nullable enable
using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;

namespace Wino.Mail.ViewModels;

public sealed partial class DailyBriefingItem : ObservableObject
{
    public DailyBriefingItem(DailyBriefingFact fact, DailyBriefingAccount account,
        CalendarEventComposeNavigationArgs? calendarArgs, string verificationCode)
    {
        Fact = fact;
        Account = account;
        CalendarArgs = calendarArgs;
        VerificationCode = verificationCode;
        IsIgnored = fact.IsIgnored;
    }

    public DailyBriefingFact Fact { get; }
    public DailyBriefingAccount Account { get; }
    public CalendarEventComposeNavigationArgs? CalendarArgs { get; }
    public string VerificationCode { get; }
    public Guid LocalAccountId => Fact.LocalAccountId;
    public Guid BriefingId => Fact.Fact.BriefingId;
    public Guid MailUniqueId => Fact.MailUniqueId;
    public long ArtifactRevision => Fact.ArtifactRevision;
    public bool IsPriority => Fact.IsPriorityVisible && Fact.Fact.Urgency is MailPriority.Urgent or MailPriority.High;
    public DailyBriefingTone Tone => Fact.Fact switch
    {
        SecurityFactPayload or AccountFactPayload => DailyBriefingTone.Critical,
        FinanceFactPayload or PurchaseFactPayload or SubscriptionFactPayload => DailyBriefingTone.Caution,
        TravelFactPayload or ReservationFactPayload => DailyBriefingTone.Success,
        ConversationFactPayload or SocialFactPayload => DailyBriefingTone.Attention,
        TaskFactPayload or ApprovalFactPayload or MeetingFactPayload => DailyBriefingTone.Critical,
        _ when Fact.Fact.Status is BriefingStatus.AwaitingMyReply or BriefingStatus.AwaitingOthers => DailyBriefingTone.Caution,
        _ => DailyBriefingTone.Neutral,
    };
    public IReadOnlyList<SmartLabelScore> SmartLabels => Fact.IncludedSmartLabels;
    public DailyBriefingIndicatorState IndicatorState => Fact.IndicatorState ?? DailyBriefingIndicatorState.AllVisible(Fact.SmartLabels);
    public bool HasIgnoreAction => BriefingId != Guid.Empty;
    public bool CanOpen => MailUniqueId != Guid.Empty;

    [ObservableProperty]
    public partial bool IsIgnored { get; set; }

    [ObservableProperty]
    public partial bool IsIgnorePending { get; set; }

    [ObservableProperty]
    public partial bool IsDeletePending { get; set; }

    public bool CanToggleIgnore => HasIgnoreAction && !IsIgnorePending && !IsDeletePending;
    public bool CanDelete => HasIgnoreAction && !IsDeletePending && !IsIgnorePending;
    public string DeleteActionText => Translator.Buttons_Delete;
    public string IgnoreActionText => IsIgnored ? Translator.DailyBriefing_ActionUnignore : Translator.DailyBriefing_ActionIgnore;
    public string IgnoreActionAutomationId => IsIgnored ? "DailyBriefingUnignoreButton" : "DailyBriefingIgnoreButton";
    public string IgnoreActionGlyph => IsIgnored ? DailyBriefingIcons.Show : DailyBriefingIcons.Hide;
    public DailyBriefingActionPresentation Action => DailyBriefingActionPresentationFactory.Create(
        Fact.Fact.PrimaryAction,
        canAddToCalendar: CalendarArgs is not null,
        hasVerificationCode: !string.IsNullOrWhiteSpace(VerificationCode),
        allowReplyAction: IndicatorState.IsNeedsReplyVisible);
    public bool ShowOpenAction => CanOpen && Action.Execution != DailyBriefingActionExecution.OpenSource;

    partial void OnIsIgnoredChanged(bool value)
    {
        OnPropertyChanged(nameof(IgnoreActionText));
        OnPropertyChanged(nameof(IgnoreActionAutomationId));
        OnPropertyChanged(nameof(IgnoreActionGlyph));
    }

    partial void OnIsIgnorePendingChanged(bool value) => OnPropertyChanged(nameof(CanToggleIgnore));

    partial void OnIsDeletePendingChanged(bool value) => OnPropertyChanged(nameof(CanDelete));
}
