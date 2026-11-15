using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.ViewModels;
using Wino.Presentation.UIKit;
#if DEBUG
using Wino.Mail.UIKit.Infrastructure;
#endif

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// The calendar invitation card of the reader (Windows MailInvitationCard, Mac .Invitation partial): a compact header
/// row with the event title and its summary or status, the answer buttons and Open in calendar (their own row on
/// iPhone), and a body that the header expands with the status, the event facts, the reply message and Remove from
/// calendar.
/// </summary>
public sealed partial class MailRenderingPageViewController
{
    private WinoSurfaceView _invitationCard = null!;
    private WinoIconView _invitationIcon = null!;
    private UILabel _invitationTitle = null!;
    private UILabel _invitationDetail = null!;
    private UIStackView _invitationBusyRow = null!;
    private UIActivityIndicatorView _invitationSpinner = null!;
    private UIView _invitationActions = null!;
    private UIButton _invitationAccept = null!;
    private UIButton _invitationTentative = null!;
    private UIButton _invitationDecline = null!;
    private UIButton _invitationOpen = null!;
    private UIButton _invitationChevron = null!;
    private UIView _invitationErrorHost = null!;
    private WinoInfoBar _invitationError = null!;
    private UIView _invitationBodySeparator = null!;
    private UIView _invitationBody = null!;
    private UIView _invitationStatusBlock = null!;
    private UIView _invitationFactsCard = null!;
    private UILabel _invitationStatus = null!;
    private UILabel _invitationReason = null!;
    private UIView _invitationDateRow = null!;
    private UILabel _invitationDate = null!;
    private UIView _invitationRecurringRow = null!;
    private UIView _invitationLocationRow = null!;
    private UILabel _invitationLocation = null!;
    private UIView _invitationOrganizerRow = null!;
    private UILabel _invitationOrganizer = null!;
    private UIView _invitationCommentBlock = null!;
    private UITextField _invitationComment = null!;
    private UIButton _invitationRemove = null!;
    private bool _invitationExpanded;
    private bool _invitationQueued;
    private bool _invitationCollapsePending;
    private bool _invitationDisposed;

    private MailInvitationCardViewModel Invitation => ViewModel.Invitation;

    private UIView BuildInvitationCard()
    {
        var card = _invitationCard = new WinoSurfaceView
        {
            CornerRadius = WinoStyle.GroupRadius,
            Fill = WinoBriefingCardView.CardFill,
            Stroke = WinoStyle.ZoneStroke,
            StrokeWidth = 0.5,
            Hidden = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        card.AccessibilityLabel = Translator.MailInvitation_CardAutomationName;

        // ---- Header row: icon, title over the summary or status (or busy), [answers, Open in calendar on iPad], chevron ----
        _invitationIcon = new WinoIconView(WinoIconGlyph.Calendar, 18, WinoStyle.Accent);
        _invitationIcon.SetContentHuggingPriority(1000, UILayoutConstraintAxis.Horizontal);
        _invitationIcon.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Horizontal);
        _invitationTitle = WinoStyle.Label(string.Empty, WinoStyle.Scaled(15, UIFontWeight.Semibold));
        _invitationTitle.LineBreakMode = UILineBreakMode.TailTruncation;
        _invitationDetail = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText);
        _invitationDetail.LineBreakMode = UILineBreakMode.TailTruncation;

        _invitationSpinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = false, TranslatesAutoresizingMaskIntoConstraints = false };
        _invitationSpinner.Transform = CoreGraphics.CGAffineTransform.MakeScale(0.7f, 0.7f);
        var busyText = WinoStyle.Label(Translator.MailInvitation_Busy, WinoStyle.Description, WinoStyle.SecondaryText);
        _invitationBusyRow = WinoLayout.HStack(4, _invitationSpinner, busyText, WinoLayout.Spacer());
        _invitationBusyRow.Alignment = UIStackViewAlignment.Center;
        _invitationBusyRow.Hidden = true;

        var text = WinoLayout.VStack(2, _invitationTitle, _invitationDetail, _invitationBusyRow);
        text.Alignment = UIStackViewAlignment.Fill;
        text.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        text.SetContentCompressionResistancePriority(200, UILayoutConstraintAxis.Horizontal);

        var toggleZone = WinoLayout.HStack(10, _invitationIcon, text);
        toggleZone.Alignment = UIStackViewAlignment.Center;
        toggleZone.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        toggleZone.SetContentCompressionResistancePriority(200, UILayoutConstraintAxis.Horizontal);
        toggleZone.AddGestureRecognizer(new UITapGestureRecognizer(ToggleInvitation));
        toggleZone.IsAccessibilityElement = true;
        toggleZone.AccessibilityTraits = UIAccessibilityTrait.Button;

        _invitationAccept = InvitationAnswerButton(AttendeeStatus.Accepted);
        _invitationTentative = InvitationAnswerButton(AttendeeStatus.Tentative);
        _invitationDecline = InvitationAnswerButton(AttendeeStatus.Declined);

        var open = UIButtonConfiguration.PlainButtonConfiguration;
        open.Title = Translator.MailInvitation_OpenInCalendar;
        open.Image = WinoIcons.Image(WinoIconGlyph.Calendar, 14, null, Translator.MailInvitation_OpenInCalendar);
        open.ImagePadding = 6;
        open.ContentInsets = new NSDirectionalEdgeInsets(6, IsPhone ? 0 : 8, 6, 8);
        open.TitleTextAttributesTransformer = attributes =>
        {
            attributes[UIStringAttributeKey.Font] = WinoStyle.Description;
            return attributes;
        };
        _invitationOpen = new UIButton(UIButtonType.System) { Configuration = open, Hidden = true, TranslatesAutoresizingMaskIntoConstraints = false };
        _invitationOpen.SetContentCompressionResistancePriority(760, UILayoutConstraintAxis.Horizontal);
        _invitationOpen.PrimaryActionTriggered += (_, _) => RunInvitationCommand(Invitation.OpenInCalendarCommand, null);

        var chevron = UIButtonConfiguration.PlainButtonConfiguration;
        chevron.Image = WinoIcons.Image(WinoIconGlyph.ChevronDown, 12, WinoStyle.SecondaryText);
        chevron.ContentInsets = new NSDirectionalEdgeInsets(8, 8, 8, 8);
        _invitationChevron = new UIButton(UIButtonType.System) { Configuration = chevron, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(_invitationChevron, 36, 36);
        _invitationChevron.PrimaryActionTriggered += (_, _) => ToggleInvitation();

        var answers = WinoLayout.HStack(8, _invitationAccept, _invitationTentative, _invitationDecline);
        UIStackView headerRow;
        if (IsPhone)
        {
            // iPhone: the answers fill their own row under the header; Open in calendar follows them.
            answers.Distribution = UIStackViewDistribution.FillEqually;
            var actions = WinoLayout.VStack(4, answers, _invitationOpen);
            actions.Alignment = UIStackViewAlignment.Leading;
            _invitationActions = WinoLayout.Insets(actions, 0, 12, 10, 12);
            answers.WidthAnchor.ConstraintEqualTo(actions.LayoutMarginsGuide.WidthAnchor).Active = true;
            headerRow = WinoLayout.HStack(6, toggleZone, _invitationChevron);
        }
        else
        {
            _invitationActions = new UIView { Hidden = true };
            headerRow = WinoLayout.HStack(6, toggleZone, answers, _invitationOpen, _invitationChevron);
        }
        headerRow.Alignment = UIStackViewAlignment.Center;
        var headerHost = WinoLayout.Insets(headerRow, 8, 12, 8, 6);
        headerHost.HeightAnchor.ConstraintGreaterThanOrEqualTo(52).Active = true;

        // Failures stay visible while the card is collapsed; the answer buttons that caused them are in the header.
        _invitationError = new WinoInfoBar(WinoInfoBarSeverity.Error, Translator.MailInvitation_ActionFailedTitle) { IsClosable = false };
        _invitationErrorHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        WinoLayout.Fill(_invitationError, _invitationErrorHost, 0, 8, 8, 8);

        // ---- Body: status and reason, the event facts, the reply message and Remove from calendar ----
        _invitationBodySeparator = new UIView { BackgroundColor = WinoStyle.ZoneStroke, Hidden = true, TranslatesAutoresizingMaskIntoConstraints = false };
        _invitationBodySeparator.HeightAnchor.ConstraintEqualTo(0.5f).Active = true;

        _invitationStatus = WinoStyle.Label(string.Empty, WinoStyle.Scaled(15, UIFontWeight.Semibold), WinoStyle.PrimaryText, 0);
        _invitationReason = WinoStyle.Label(string.Empty, WinoStyle.Subheadline, WinoStyle.SecondaryText, 0);
        var statusBlock = WinoLayout.VStack(2, _invitationStatus, _invitationReason);
        statusBlock.Alignment = UIStackViewAlignment.Fill;
        _invitationStatusBlock = statusBlock;

        _invitationDate = WinoStyle.Label(string.Empty, WinoStyle.Subheadline, WinoStyle.PrimaryText, 0);
        _invitationDateRow = FactRow(WinoIconGlyph.Clock, _invitationDate);
        _invitationRecurringRow = FactRow(WinoIconGlyph.CalendarEventRepeat, WinoStyle.Label(Translator.MailInvitation_Recurring, WinoStyle.Subheadline, WinoStyle.PrimaryText, 0));
        _invitationLocation = WinoStyle.Label(string.Empty, WinoStyle.Subheadline, WinoStyle.PrimaryText, 0);
        _invitationLocationRow = FactRow(WinoIconGlyph.Location, _invitationLocation);
        _invitationOrganizer = WinoStyle.Label(string.Empty, WinoStyle.Subheadline, WinoStyle.PrimaryText, 0);
        _invitationOrganizerRow = FactRow(WinoIconGlyph.Person, _invitationOrganizer);
        var facts = WinoLayout.VStack(8, _invitationDateRow, _invitationRecurringRow, _invitationLocationRow, _invitationOrganizerRow);
        facts.Alignment = UIStackViewAlignment.Fill;
        var factsCard = new WinoSurfaceView { CornerRadius = 8, Fill = WinoStyle.SubtleFill, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(facts, factsCard, 10, 12, 10, 12);
        _invitationFactsCard = factsCard;

        var commentLabel = WinoStyle.Label(Translator.CalendarEventRsvpPanel_Message, WinoStyle.Description, WinoStyle.SecondaryText);
        _invitationComment = new UITextField
        {
            Placeholder = Translator.CalendarEventRsvpPanel_AddMessage,
            Font = WinoStyle.Body,
            BorderStyle = UITextBorderStyle.RoundedRect,
            ReturnKeyType = UIReturnKeyType.Done,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _invitationComment.HeightAnchor.ConstraintGreaterThanOrEqualTo(40).Active = true;
        WinoAccessibility.Label(_invitationComment, Translator.CalendarEventRsvpPanel_Message);
        _invitationComment.EditingChanged += (_, _) =>
        {
            var value = _invitationComment.Text ?? string.Empty;
            if (!_invitationDisposed && Invitation.Comment != value) Invitation.Comment = value;
        };
        _invitationComment.ShouldReturn = field =>
        {
            field.ResignFirstResponder();
            return true;
        };
        var commentBlock = WinoLayout.VStack(6, commentLabel, _invitationComment);
        commentBlock.Alignment = UIStackViewAlignment.Fill;
        _invitationCommentBlock = commentBlock;

        var remove = UIButtonConfiguration.GrayButtonConfiguration;
        remove.Title = Translator.MailInvitation_RemoveFromCalendar;
        remove.Image = WinoIcons.Image(WinoIconGlyph.Delete, 14, null, Translator.MailInvitation_RemoveFromCalendar);
        remove.ImagePadding = 6;
        remove.CornerStyle = UIButtonConfigurationCornerStyle.Medium;
        _invitationRemove = new UIButton(UIButtonType.System) { Configuration = remove, Hidden = true, TranslatesAutoresizingMaskIntoConstraints = false };
        _invitationRemove.PrimaryActionTriggered += (_, _) => RunInvitationCommand(Invitation.RemoveFromCalendarCommand, null);

        var body = WinoLayout.VStack(12, statusBlock, factsCard, commentBlock, _invitationRemove);
        body.Alignment = UIStackViewAlignment.Leading;
        foreach (var row in new UIView[] { statusBlock, factsCard, commentBlock })
            row.WidthAnchor.ConstraintEqualTo(body.WidthAnchor).Active = true;
        _invitationBody = new UIView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        WinoLayout.Fill(body, _invitationBody, 12);

        var root = WinoLayout.VStack(0, headerHost, _invitationActions, _invitationErrorHost, _invitationBodySeparator, _invitationBody);
        root.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(root, card);

        Invitation.PropertyChanged += InvitationPropertyChanged;
        ApplyInvitation();
#if DEBUG
        UIKitDebugBridge.Register("reader-invitation", args => ReaderDebugAsync(() =>
        {
            if (args.Length > 0 && (args[0] == "expand") != _invitationExpanded) ToggleInvitation();
            var vm = Invitation;
            return $"visible={vm.IsVisible} cardHidden={_invitationCard.Hidden} expanded={_invitationExpanded} title=\"{vm.Title}\" icon={vm.HeaderIcon} " +
                   $"detail=\"{_invitationDetail.Text}\" canRespond={vm.CanRespond} busy={vm.IsBusy} accepted={vm.IsAcceptedResponse} " +
                   $"tentative={vm.IsTentativeResponse} declined={vm.IsDeclinedResponse} open={vm.CanOpenInCalendar} remove={vm.CanRemoveFromCalendar} " +
                   $"status=\"{vm.StatusText}\" reason=\"{vm.ReasonText}\" date=\"{vm.DateRangeText}\" location=\"{vm.Location}\" organizer=\"{vm.OrganizerText}\" " +
                   $"recurring={vm.IsRecurring} error=\"{vm.ErrorMessage}\" disposed={_invitationDisposed}";
        }));
#endif
        return card;
    }

    private UIButton InvitationAnswerButton(AttendeeStatus response)
    {
        var title = AnswerTitle(response);
        var button = new UIButton(UIButtonType.System) { Hidden = true, TranslatesAutoresizingMaskIntoConstraints = false };
        ApplyAnswer(button, response, false);
        WinoAccessibility.Label(button, title);
        button.SetContentCompressionResistancePriority(760, UILayoutConstraintAxis.Horizontal);
        button.SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
        button.PrimaryActionTriggered += (_, _) => RunInvitationCommand(Invitation.RespondCommand, response);
        return button;
    }

    private static string AnswerTitle(AttendeeStatus response) => response switch
    {
        AttendeeStatus.Accepted => Translator.CalendarEventRsvpPanel_Accept,
        AttendeeStatus.Tentative => Translator.CalendarEventRsvpPanel_Tentative,
        _ => Translator.CalendarEventRsvpPanel_Decline
    };

    private static WinoIconGlyph AnswerGlyph(AttendeeStatus response) => response switch
    {
        AttendeeStatus.Accepted => WinoIconGlyph.EventAccept,
        AttendeeStatus.Tentative => WinoIconGlyph.EventTentative,
        _ => WinoIconGlyph.EventDecline
    };

    private static UIColor AnswerTint(AttendeeStatus response) => response switch
    {
        AttendeeStatus.Accepted => WinoStyle.Success,
        AttendeeStatus.Tentative => WinoStyle.Caution,
        _ => WinoStyle.Critical
    };

    /// <summary>The current answer is a tinted button with a checkmark (Windows marks it the same way); the others are gray.</summary>
    private static void ApplyAnswer(UIButton button, AttendeeStatus response, bool isCurrent)
    {
        var configuration = isCurrent ? UIButtonConfiguration.TintedButtonConfiguration : UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Title = AnswerTitle(response);
        configuration.Image = WinoIcons.Image(isCurrent ? WinoIconGlyph.Checkmark : AnswerGlyph(response), 12, AnswerTint(response));
        configuration.ImagePadding = 5;
        configuration.CornerStyle = UIButtonConfigurationCornerStyle.Medium;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(7, 10, 7, 10);
        if (isCurrent) configuration.BaseForegroundColor = WinoStyle.PrimaryText;
        configuration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        configuration.TitleTextAttributesTransformer = attributes =>
        {
            attributes[UIStringAttributeKey.Font] = WinoStyle.Scaled(14, UIFontWeight.Medium);
            return attributes;
        };
        button.Configuration = configuration;
        button.AccessibilityTraits = isCurrent ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
    }

    private static UIView FactRow(WinoIconGlyph glyph, UILabel label)
    {
        var icon = new WinoIconView(glyph, 15, WinoStyle.SecondaryText);
        icon.SetContentHuggingPriority(1000, UILayoutConstraintAxis.Horizontal);
        label.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        var row = WinoLayout.HStack(10, icon, label);
        row.Alignment = UIStackViewAlignment.FirstBaseline;
        return row;
    }

    private void ToggleInvitation()
    {
        if (_invitationDisposed) return;
        _invitationExpanded = !_invitationExpanded;
        if (!_invitationExpanded) _invitationComment.ResignFirstResponder();
        ApplyInvitation();
    }

    private void RunInvitationCommand(IAsyncRelayCommand command, object? parameter)
    {
        if (_invitationDisposed || !command.CanExecute(parameter)) return;
        _invitationComment.ResignFirstResponder();
        Observe(command.ExecuteAsync(parameter));
    }

    private void InvitationPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        // A hidden card is a new message: the next one opens collapsed.
        if (args.PropertyName == nameof(MailInvitationCardViewModel.IsVisible) && sender is MailInvitationCardViewModel { IsVisible: false })
            _invitationCollapsePending = true;
        // The properties change one by one while an invitation loads; one update per main-queue turn.
        if (_invitationQueued) return;
        _invitationQueued = true;
        _ = Dispatcher.ExecuteOnUIThread(() =>
        {
            _invitationQueued = false;
            ApplyInvitation();
        });
    }

    private void ApplyInvitation()
    {
        if (_invitationDisposed) return;
        var vm = Invitation;
        if (_invitationCollapsePending)
        {
            _invitationCollapsePending = false;
            _invitationExpanded = false;
        }

        _invitationCard.Hidden = !vm.IsVisible;
        if (!vm.IsVisible)
        {
            _invitationSpinner.StopAnimating();
            return;
        }

        _invitationIcon.Icon = vm.HeaderIcon;
        _invitationTitle.Text = vm.Title ?? string.Empty;
        var detail = vm.CanRespond || string.IsNullOrEmpty(vm.StatusText) ? vm.HeaderSummaryText : vm.StatusText;
        _invitationDetail.Text = detail ?? string.Empty;
        _invitationDetail.Hidden = vm.IsBusy || string.IsNullOrEmpty(detail);
        _invitationBusyRow.Hidden = !vm.IsBusy;
        if (vm.IsBusy) _invitationSpinner.StartAnimating();
        else _invitationSpinner.StopAnimating();

        foreach (var (button, response, isCurrent) in new[]
        {
            (_invitationAccept, AttendeeStatus.Accepted, vm.IsAcceptedResponse),
            (_invitationTentative, AttendeeStatus.Tentative, vm.IsTentativeResponse),
            (_invitationDecline, AttendeeStatus.Declined, vm.IsDeclinedResponse)
        })
        {
            button.Hidden = !vm.CanRespond;
            button.Enabled = vm.CanRespond && !vm.IsBusy;
            ApplyAnswer(button, response, isCurrent);
        }
        _invitationOpen.Hidden = !vm.CanOpenInCalendar;
        _invitationOpen.Enabled = vm.CanOpenInCalendar && !vm.IsBusy;
        if (IsPhone) _invitationActions.Hidden = !vm.CanRespond && !vm.CanOpenInCalendar;

        _invitationError.Message = vm.ErrorMessage;
        _invitationErrorHost.Hidden = !vm.HasError;

        var expanded = _invitationExpanded;
        if (_invitationChevron.Configuration is { } chevron)
        {
            chevron.Image = WinoIcons.Image(expanded ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown, 12, WinoStyle.SecondaryText);
            _invitationChevron.Configuration = chevron;
        }
        var expansion = expanded ? Translator.WinoIntelligence_Expanded : Translator.WinoIntelligence_Collapsed;
        WinoAccessibility.Label(_invitationChevron, $"{Translator.MailInvitation_CardAutomationName}, {expansion}");
        if (_invitationIcon.Superview is { } toggleZone)
        {
            toggleZone.AccessibilityLabel = string.Join(", ", new[] { vm.Title, _invitationDetail.Hidden ? Translator.MailInvitation_Busy : detail }.Where(static part => !string.IsNullOrEmpty(part)));
            toggleZone.AccessibilityValue = expansion;
        }
        _invitationBodySeparator.Hidden = !expanded;
        _invitationBody.Hidden = !expanded;
        if (!expanded) return;

        _invitationStatus.Text = vm.StatusText ?? string.Empty;
        _invitationStatus.Hidden = !vm.HasStatus;
        _invitationReason.Text = vm.ReasonText ?? string.Empty;
        _invitationReason.Hidden = !vm.HasReason;
        _invitationStatusBlock.Hidden = !vm.HasStatus && !vm.HasReason;
        _invitationDate.Text = vm.DateRangeText ?? string.Empty;
        _invitationDateRow.Hidden = !vm.HasDateRange;
        _invitationRecurringRow.Hidden = !vm.IsRecurring;
        _invitationLocation.Text = vm.Location ?? string.Empty;
        _invitationLocationRow.Hidden = !vm.HasLocation;
        _invitationOrganizer.Text = vm.OrganizerText ?? string.Empty;
        _invitationOrganizerRow.Hidden = !vm.HasOrganizer;
        _invitationFactsCard.Hidden = !vm.HasDateRange && !vm.IsRecurring && !vm.HasLocation && !vm.HasOrganizer;

        _invitationCommentBlock.Hidden = !vm.CanRespond;
        _invitationComment.Enabled = vm.CanEditComment;
        if ((_invitationComment.Text ?? string.Empty) != (vm.Comment ?? string.Empty)) _invitationComment.Text = vm.Comment ?? string.Empty;
        _invitationRemove.Hidden = !vm.CanRemoveFromCalendar;
        _invitationRemove.Enabled = vm.CanRemoveFromCalendar && !vm.IsBusy;
    }

    /// <summary>The reader is leaving or being disposed. Safe to call more than once.</summary>
    private void InvitationDispose()
    {
        if (_invitationDisposed) return;
        _invitationDisposed = true;
        Invitation.PropertyChanged -= InvitationPropertyChanged;
        _invitationSpinner?.StopAnimating();
    }
}
