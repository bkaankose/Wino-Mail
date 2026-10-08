#nullable enable
using System.Collections.Generic;
using System.Linq;
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

/// <summary>Localized, UI-ready form of the primary smart action for one message.</summary>
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
/// Maps the typed smart actions Enrichment extracted to the card's primary button.
/// Two of them Wino can carry out on its own; the rest name what the message asks for and
/// hand over to the message itself, because nothing local can complete them yet.
/// </summary>
public static class DailyBriefingActionPresentationFactory
{
    public static DailyBriefingActionPresentation Open { get; } = Source(
        Translator.DailyBriefing_ActionOpen, DailyBriefingIcons.OpenMail, "Open");

    /// <summary>
    /// Picks the card's primary action from Enrichment's smart actions. The order puts what
    /// Wino can finish itself first, then what is time-bound, then the rest.
    /// </summary>
    public static DailyBriefingActionPresentation ForActions(IReadOnlyList<MailSmartAction> actions, bool allowReply = true)
    {
        var name = actions
            .Select(ActionName)
            .Where(static entry => entry.Name is not null)
            .OrderBy(static entry => entry.Rank)
            .Select(static entry => entry.Name)
            .FirstOrDefault();

        return Create(name, allowReply);
    }

    private static (string? Name, int Rank) ActionName(MailSmartAction action) => action switch
    {
        OneTimeCodeAction => ("copycode", 0),
        SuggestedReplyAction => ("reply", 1),
        CalendarEventAction => ("addtocalendar", 2),
        PaymentDueAction => ("pay", 3),
        ShipmentAction => ("trackshipment", 4),
        SecurityNoticeAction => ("review", 5),
        BookingAction or OrderAction or TaskAction => ("review", 6),
        _ => (null, int.MaxValue),
    };

    /// <summary>
    /// Builds the presentation for an action name. An action this build does not know
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
