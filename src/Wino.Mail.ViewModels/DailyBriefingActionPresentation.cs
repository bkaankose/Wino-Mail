using Wino.Core.Domain;
using Wino.Mail.AI.Abstractions;

namespace Wino.Mail.ViewModels;

/// <summary>Execution route for an action exposed by a briefing card.</summary>
public enum DailyBriefingActionExecution
{
    /// <summary>No typed primary action is displayed.</summary>
    None,

    /// <summary>Open the source message so the user can complete the action there.</summary>
    OpenSource,

    /// <summary>Create a reply draft through Wino's existing reply flow.</summary>
    Reply,

    /// <summary>Copy a verification code to the clipboard.</summary>
    CopyVerificationCode,

    /// <summary>Open Calendar compose with the extracted temporal information.</summary>
    AddToCalendar,
}

/// <summary>Localized, UI-ready representation of a typed briefing action payload.</summary>
public sealed record DailyBriefingActionPresentation(
    string Label,
    string Glyph,
    string AutomationId,
    DailyBriefingActionExecution Execution)
{
    /// <summary>Presentation used by <see cref="NoActionPayload"/>.</summary>
    public static DailyBriefingActionPresentation None { get; } =
        new(string.Empty, string.Empty, string.Empty, DailyBriefingActionExecution.None);

    /// <summary>Whether the action should be rendered as the primary card command.</summary>
    public bool IsVisible => Execution != DailyBriefingActionExecution.None;

    /// <summary>Whether the action can be completed directly by Wino.</summary>
    public bool IsNative => Execution is DailyBriefingActionExecution.Reply
        or DailyBriefingActionExecution.CopyVerificationCode
        or DailyBriefingActionExecution.AddToCalendar;

    /// <summary>Whether the action continues in its source message.</summary>
    public bool IsSource => Execution == DailyBriefingActionExecution.OpenSource;
}

/// <summary>Maps every supported briefing action payload to its card presentation and behavior.</summary>
public static class DailyBriefingActionPresentationFactory
{
    /// <summary>
    /// Creates a presentation for the supplied payload. Payloads introduced by a future contract
    /// version safely degrade to opening the source message.
    /// </summary>
    public static DailyBriefingActionPresentation Create(
        BriefingActionPayload action,
        bool canAddToCalendar = true,
        bool hasVerificationCode = true,
        bool allowReplyAction = true)
        => action switch
        {
            NoActionPayload => DailyBriefingActionPresentation.None,
            ReplyActionPayload when allowReplyAction => Native(Translator.DailyBriefing_ActionReply, DailyBriefingIcons.Reply,
                "DailyBriefingReplyButton", DailyBriefingActionExecution.Reply),
            ReplyActionPayload => Source(Translator.DailyBriefing_ActionOpen, DailyBriefingIcons.OpenMail, "OpenReplySource"),
            CopyVerificationCodeActionPayload when hasVerificationCode => Native(
                Translator.Intelligence_ActionCopyCode, DailyBriefingIcons.Copy,
                "DailyBriefingCopyCodeButton", DailyBriefingActionExecution.CopyVerificationCode),
            AddToCalendarActionPayload when canAddToCalendar => Native(
                Translator.DailyBriefing_ActionAddToCalendar, DailyBriefingIcons.AddToCalendar,
                "DailyBriefingAddToCalendarButton", DailyBriefingActionExecution.AddToCalendar),
            AcceptInvitationActionPayload => Source(Translator.DailyBriefing_ActionAcceptInvitation, DailyBriefingIcons.Accept, "AcceptInvitation"),
            AddToCalendarActionPayload => Source(Translator.DailyBriefing_ActionAddToCalendar, DailyBriefingIcons.AddToCalendar, "AddToCalendar"),
            ApproveActionPayload => Source(Translator.DailyBriefing_ActionApprove, DailyBriefingIcons.Approve, "Approve"),
            CancelReservationActionPayload => Source(Translator.DailyBriefing_ActionCancelReservation, DailyBriefingIcons.Cancel, "CancelReservation"),
            CancelSubscriptionActionPayload => Source(Translator.DailyBriefing_ActionCancelSubscription, DailyBriefingIcons.Cancel, "CancelSubscription"),
            ChangePasswordActionPayload => Source(Translator.DailyBriefing_ActionChangePassword, DailyBriefingIcons.ChangePassword, "ChangePassword"),
            CheckInActionPayload => Source(Translator.DailyBriefing_ActionCheckIn, DailyBriefingIcons.CheckIn, "CheckIn"),
            CompleteTaskActionPayload => Source(Translator.DailyBriefing_ActionCompleteTask, DailyBriefingIcons.Complete, "CompleteTask"),
            ConfirmActionPayload => Source(Translator.DailyBriefing_ActionConfirm, DailyBriefingIcons.Confirm, "Confirm"),
            CopyVerificationCodeActionPayload => Source(Translator.DailyBriefing_ActionOpen, DailyBriefingIcons.OpenMail, "OpenForCode"),
            DeclineInvitationActionPayload => Source(Translator.DailyBriefing_ActionDeclineInvitation, DailyBriefingIcons.Decline, "DeclineInvitation"),
            DownloadAttachmentActionPayload => Source(Translator.DailyBriefing_ActionDownloadAttachment, DailyBriefingIcons.Download, "DownloadAttachment"),
            FollowUpActionPayload => Source(Translator.DailyBriefing_ActionFollowUp, DailyBriefingIcons.FollowUp, "FollowUp"),
            OpenMagicSignInLinkActionPayload => Source(Translator.DailyBriefing_ActionOpenSignInLink, DailyBriefingIcons.Link, "OpenSignInLink"),
            OpenRelevantLinkActionPayload => Source(Translator.DailyBriefing_ActionOpenRelevantLink, DailyBriefingIcons.Link, "OpenRelevantLink"),
            PayActionPayload => Source(Translator.DailyBriefing_ActionPay, DailyBriefingIcons.Pay, "Pay"),
            RejectActionPayload => Source(Translator.DailyBriefing_ActionReject, DailyBriefingIcons.Decline, "Reject"),
            RenewActionPayload => Source(Translator.DailyBriefing_ActionRenew, DailyBriefingIcons.Renew, "Renew"),
            ReportPhishingActionPayload => Source(Translator.DailyBriefing_ActionReportPhishing, DailyBriefingIcons.ReportPhishing, "ReportPhishing"),
            RescheduleActionPayload => Source(Translator.DailyBriefing_ActionReschedule, DailyBriefingIcons.Reschedule, "Reschedule"),
            RespondTentativeActionPayload => Source(Translator.DailyBriefing_ActionRespondTentative, DailyBriefingIcons.FollowUp, "RespondTentative"),
            ReviewAccountActivityActionPayload => Source(Translator.DailyBriefing_ActionReviewAccountActivity, DailyBriefingIcons.Review, "ReviewAccountActivity"),
            ReviewActionPayload => Source(Translator.DailyBriefing_ActionReview, DailyBriefingIcons.Review, "Review"),
            ReviewInvoiceActionPayload => Source(Translator.DailyBriefing_ActionReviewInvoice, DailyBriefingIcons.Pay, "ReviewInvoice"),
            SignActionPayload => Source(Translator.DailyBriefing_ActionSign, DailyBriefingIcons.Sign, "Sign"),
            SubmitActionPayload => Source(Translator.DailyBriefing_ActionSubmit, DailyBriefingIcons.Submit, "Submit"),
            TrackShipmentActionPayload => Source(Translator.DailyBriefing_ActionTrackShipment, DailyBriefingIcons.TrackShipment, "TrackShipment"),
            UnsubscribeActionPayload => Source(Translator.DailyBriefing_ActionUnsubscribe, DailyBriefingIcons.Unsubscribe, "Unsubscribe"),
            VerifyAccountActionPayload => Source(Translator.DailyBriefing_ActionVerifyAccount, DailyBriefingIcons.Verify, "VerifyAccount"),
            ViewCalendarEventActionPayload => Source(Translator.DailyBriefing_ActionViewCalendarEvent, DailyBriefingIcons.AddToCalendar, "ViewCalendarEvent"),
            ViewDocumentActionPayload => Source(Translator.DailyBriefing_ActionViewDocument, DailyBriefingIcons.ViewDocument, "ViewDocument"),
            ViewItineraryActionPayload => Source(Translator.DailyBriefing_ActionViewItinerary, DailyBriefingIcons.ViewItinerary, "ViewItinerary"),
            ViewOrderActionPayload => Source(Translator.DailyBriefing_ActionViewOrder, DailyBriefingIcons.ViewOrder, "ViewOrder"),
            ViewReservationActionPayload => Source(Translator.DailyBriefing_ActionViewReservation, DailyBriefingIcons.ViewReservation, "ViewReservation"),
            _ => Source(Translator.DailyBriefing_ActionOpen, DailyBriefingIcons.OpenMail, "OpenFallback"),
        };

    private static DailyBriefingActionPresentation Native(string label, string glyph, string automationId,
        DailyBriefingActionExecution execution)
        => new(label, glyph, automationId, execution);

    private static DailyBriefingActionPresentation Source(string label, string glyph, string automationSuffix)
        => new(label, glyph, $"DailyBriefing{automationSuffix}Button", DailyBriefingActionExecution.OpenSource);
}
