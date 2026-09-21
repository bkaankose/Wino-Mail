#nullable enable
using Wino.Core.Domain;
using Wino.Mail.AI.Abstractions;

namespace Wino.Mail.ViewModels;

/// <summary>Execution route for the primary action on a briefing card.</summary>
public enum DailyBriefingActionExecution
{
    /// <summary>Open the source message so the user completes the action there.</summary>
    OpenSource,

    /// <summary>Create a reply draft through Wino's existing reply flow.</summary>
    Reply,

    /// <summary>Copy the verification code the message carries.</summary>
    CopyVerificationCode,
}

/// <summary>Localized, UI-ready form of the action Classification chose for one message.</summary>
public sealed record DailyBriefingActionPresentation(
    string Label,
    string Glyph,
    string AutomationId,
    DailyBriefingActionExecution Execution)
{
    /// <summary>Whether Wino completes the action itself instead of handing over to the message.</summary>
    public bool IsNative => Execution is DailyBriefingActionExecution.Reply
        or DailyBriefingActionExecution.CopyVerificationCode;
}

/// <summary>
/// Maps <see cref="MailBriefingAction"/> - the action Classification returns alongside the labels and the
/// priority - to the card's primary button.
/// Two of them Wino can carry out on its own; the rest name what the message asks for and
/// hand over to the message itself, because nothing local can complete them.
/// </summary>
public static class DailyBriefingActionPresentationFactory
{
    public static DailyBriefingActionPresentation Open { get; } = Source(
        Translator.DailyBriefing_ActionOpen, DailyBriefingIcons.OpenMail, "Open");

    /// <summary>
    /// Builds the presentation for a stored action name. An action this build does not know
    /// degrades to opening the message rather than disappearing.
    /// </summary>
    public static DailyBriefingActionPresentation Create(string? action, bool allowReply = true)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return Open;
        }

        return action.ToLowerInvariant() switch
        {
            "reply" when allowReply => Native(Translator.DailyBriefing_ActionReply, DailyBriefingIcons.Reply,
                "Reply", DailyBriefingActionExecution.Reply),
            "reply" => Open,
            "copycode" => Native(Translator.Intelligence_ActionCopyCode, DailyBriefingIcons.Copy,
                "CopyCode", DailyBriefingActionExecution.CopyVerificationCode),
            "pay" => Source(Translator.DailyBriefing_ActionPay, DailyBriefingIcons.Pay, "Pay"),
            "confirm" => Source(Translator.DailyBriefing_ActionConfirm, DailyBriefingIcons.Confirm, "Confirm"),
            "addtocalendar" => Source(Translator.DailyBriefing_ActionAddToCalendar, DailyBriefingIcons.Calendar, "AddToCalendar"),
            "rsvp" => Source(Translator.DailyBriefing_ActionRsvp, DailyBriefingIcons.Calendar, "Rsvp"),
            "trackshipment" => Source(Translator.DailyBriefing_ActionTrackShipment, DailyBriefingIcons.TrackShipment, "TrackShipment"),
            "review" => Source(Translator.DailyBriefing_ActionReview, DailyBriefingIcons.Review, "Review"),
            "sign" => Source(Translator.DailyBriefing_ActionSign, DailyBriefingIcons.Sign, "Sign"),
            "unsubscribe" => Source(Translator.DailyBriefing_ActionUnsubscribe, DailyBriefingIcons.Unsubscribe, "Unsubscribe"),
            "resetpassword" => Source(Translator.DailyBriefing_ActionResetPassword, DailyBriefingIcons.Security, "ResetPassword"),
            _ => Open,
        };
    }

    private static DailyBriefingActionPresentation Native(
        string label, string glyph, string automationSuffix, DailyBriefingActionExecution execution)
        => new(label, glyph, $"DailyBriefing{automationSuffix}Button", execution);

    private static DailyBriefingActionPresentation Source(string label, string glyph, string automationSuffix)
        => new(label, glyph, $"DailyBriefing{automationSuffix}Button", DailyBriefingActionExecution.OpenSource);
}
