using System.ComponentModel;
using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Extras;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// The calendar invitation card of the reader (Windows MailInvitationCard): a compact header row with the
/// event title, its summary or status, the answer buttons and Open in calendar, and a body that the
/// header expands with the status, the event facts, the reply message and Remove from calendar.
/// </summary>
public sealed partial class MailRenderingPageViewController
{
    private WinoSurfaceView _invitationCard = null!;
    private WinoIconView _invitationIcon = null!;
    private NSTextField _invitationTitle = null!;
    private NSTextField _invitationDetail = null!;
    private NSStackView _invitationBusyRow = null!;
    private NSProgressIndicator _invitationSpinner = null!;
    private NSButton _invitationAccept = null!;
    private NSButton _invitationTentative = null!;
    private NSButton _invitationDecline = null!;
    private NSButton _invitationOpen = null!;
    private NSButton _invitationChevron = null!;
    private NSView _invitationErrorHost = null!;
    private WinoInfoBar _invitationError = null!;
    private WinoSurfaceView _invitationBodySeparator = null!;
    private NSView _invitationBody = null!;
    private NSView _invitationStatusBlock = null!;
    private NSView _invitationFactsCard = null!;
    private NSTextField _invitationStatus = null!;
    private NSTextField _invitationReason = null!;
    private NSView _invitationDateRow = null!;
    private NSTextField _invitationDate = null!;
    private NSView _invitationRecurringRow = null!;
    private NSView _invitationLocationRow = null!;
    private NSTextField _invitationLocation = null!;
    private NSView _invitationOrganizerRow = null!;
    private NSTextField _invitationOrganizer = null!;
    private NSTextField _invitationComment = null!;
    private NSButton _invitationRemove = null!;
    private bool _invitationExpanded;
    private bool _invitationQueued;
    private bool _invitationCollapsePending;
    private bool _invitationDisposed;

    private MailInvitationCardViewModel Invitation => ViewModel.Invitation;

    private NSView BuildInvitationCard()
    {
        var card = _invitationCard = new WinoSurfaceView
        {
            CornerRadius = WinoStyle.GroupRadius,
            Fill = WinoBriefingCardView.CardFill,
            Stroke = WinoBriefingCardView.CardStroke,
            Hidden = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        card.AccessibilityElement = true;
        card.AccessibilityRole = NSAccessibilityRoles.GroupRole;
        card.AccessibilityLabel = Translator.MailInvitation_CardAutomationName;

        // ---- Header row: icon, title, summary or status (or busy) | answers, Open in calendar | chevron ----
        _invitationIcon = new WinoIconView(WinoIconGlyph.Calendar, 16, WinoStyle.Accent);
        _invitationTitle = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong);
        _invitationTitle.LineBreakMode = NSLineBreakMode.TruncatingTail;
        _invitationTitle.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _invitationTitle.WidthAnchor.ConstraintLessThanOrEqualTo(280).Active = true;
        _invitationDetail = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        _invitationDetail.LineBreakMode = NSLineBreakMode.TruncatingTail;
        _invitationDetail.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        _invitationDetail.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);

        _invitationSpinner = new NSProgressIndicator
        {
            Style = NSProgressIndicatorStyle.Spinning,
            ControlSize = NSControlSize.Small,
            Indeterminate = true,
            IsDisplayedWhenStopped = false,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Size(_invitationSpinner, 12, 12);
        var busyText = WinoStyle.Label(Translator.MailInvitation_Busy, WinoStyle.Caption, WinoStyle.SecondaryText);
        busyText.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        _invitationBusyRow = WinoLayout.HStack(6, _invitationSpinner, busyText);
        _invitationBusyRow.Hidden = true;

        var toggleZone = WinoLayout.HStack(8, _invitationIcon, _invitationTitle, _invitationDetail, _invitationBusyRow, WinoLayout.Spacer());
        toggleZone.Distribution = NSStackViewDistribution.Fill;
        toggleZone.DetachesHiddenViews = true;
        toggleZone.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        toggleZone.AddGestureRecognizer(new NSClickGestureRecognizer(() => ToggleInvitation()));

        _invitationAccept = InvitationAnswerButton(Translator.CalendarEventRsvpPanel_Accept, AttendeeStatus.Accepted);
        _invitationTentative = InvitationAnswerButton(Translator.CalendarEventRsvpPanel_Tentative, AttendeeStatus.Tentative);
        _invitationDecline = InvitationAnswerButton(Translator.CalendarEventRsvpPanel_Decline, AttendeeStatus.Declined);

        _invitationOpen = new NSButton
        {
            Bordered = false,
            BezelStyle = NSBezelStyle.Inline,
            AttributedTitle = new NSAttributedString(Translator.MailInvitation_OpenInCalendar, new NSStringAttributes { ForegroundColor = WinoStyle.Accent, Font = WinoStyle.Caption }),
            Hidden = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _invitationOpen.SetContentCompressionResistancePriority(760, NSLayoutConstraintOrientation.Horizontal);
        _invitationOpen.Activated += (_, _) => RunInvitationCommand(Invitation.OpenInCalendarCommand, null);

        _invitationChevron = new NSButton
        {
            Title = string.Empty,
            Bordered = false,
            Image = WinoIcons.Image(WinoIconGlyph.ChevronDown, 12),
            ImagePosition = NSCellImagePosition.ImageOnly,
            ContentTintColor = WinoStyle.SecondaryText,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Size(_invitationChevron, 24, 24);
        _invitationChevron.Activated += (_, _) => ToggleInvitation();

        var headerRow = WinoLayout.HStack(6, toggleZone, _invitationAccept, _invitationTentative, _invitationDecline, _invitationOpen, _invitationChevron);
        headerRow.Distribution = NSStackViewDistribution.Fill;
        headerRow.DetachesHiddenViews = true;
        headerRow.SetCustomSpacing(10, _invitationOpen);
        var headerHost = new InvitationHeaderRow();
        WinoLayout.Fill(headerRow, headerHost, 5, 12, 5, 6);
        headerHost.HeightAnchor.ConstraintGreaterThanOrEqualTo(40).Active = true;

        // Failures stay visible while the card is collapsed; the answer buttons that caused them are in the header.
        _invitationError = new WinoInfoBar(WinoInfoBarSeverity.Error, Translator.MailInvitation_ActionFailedTitle) { IsClosable = false };
        _invitationErrorHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        WinoLayout.Fill(_invitationError, _invitationErrorHost, 0, 8, 8, 8);

        // ---- Body: status and reason, the event facts, the reply message and Remove from calendar ----
        _invitationBodySeparator = new WinoSurfaceView { Fill = WinoStyle.ZoneStroke, Hidden = true };
        _invitationBodySeparator.HeightAnchor.ConstraintEqualTo(1).Active = true;

        _invitationStatus = WrappingLabel(WinoStyle.BodyStrong, WinoStyle.PrimaryText);
        _invitationReason = WrappingLabel(WinoStyle.Body, WinoStyle.SecondaryText);
        var statusBlock = WinoLayout.VStack(2, _invitationStatus, _invitationReason);
        _invitationStatusBlock = statusBlock;
        statusBlock.DetachesHiddenViews = true;
        FillInvitationWidth(statusBlock);

        _invitationDate = WrappingLabel(WinoStyle.Body, WinoStyle.PrimaryText, selectable: true);
        _invitationDateRow = FactRow(WinoIconGlyph.Clock, _invitationDate);
        _invitationRecurringRow = FactRow(WinoIconGlyph.CalendarEventRepeat, WrappingLabel(WinoStyle.Body, WinoStyle.PrimaryText, Translator.MailInvitation_Recurring));
        _invitationLocation = WrappingLabel(WinoStyle.Body, WinoStyle.PrimaryText, selectable: true);
        _invitationLocationRow = FactRow(WinoIconGlyph.Location, _invitationLocation);
        _invitationOrganizer = WrappingLabel(WinoStyle.Body, WinoStyle.PrimaryText, selectable: true);
        _invitationOrganizerRow = FactRow(WinoIconGlyph.Person, _invitationOrganizer);
        var facts = WinoLayout.VStack(6, _invitationDateRow, _invitationRecurringRow, _invitationLocationRow, _invitationOrganizerRow);
        facts.DetachesHiddenViews = true;
        FillInvitationWidth(facts);
        var factsCard = new WinoSurfaceView { CornerRadius = 4, Fill = WinoStyle.SubtleFill, Stroke = WinoBriefingCardView.CardStroke };
        WinoLayout.Fill(facts, factsCard, 8, 10, 8, 10);
        _invitationFactsCard = factsCard;

        _invitationComment = new NSTextField
        {
            PlaceholderString = Translator.CalendarEventRsvpPanel_AddMessage,
            Font = WinoStyle.Body,
            UsesSingleLineMode = false,
            LineBreakMode = NSLineBreakMode.ByWordWrapping,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _invitationComment.Cell.Wraps = true;
        _invitationComment.Cell.Scrollable = false;
        _invitationComment.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        _invitationComment.HeightAnchor.ConstraintLessThanOrEqualTo(120).Active = true;
        WinoAccessibility.Label(_invitationComment, Translator.CalendarEventRsvpPanel_Message);
        _invitationComment.Changed += (_, _) =>
        {
            if (!_invitationDisposed && Invitation.Comment != _invitationComment.StringValue) Invitation.Comment = _invitationComment.StringValue;
        };

        _invitationRemove = new NSButton
        {
            Title = Translator.MailInvitation_RemoveFromCalendar,
            BezelStyle = NSBezelStyle.Rounded,
            Image = WinoIcons.Image(WinoIconGlyph.Delete, 14, null, Translator.MailInvitation_RemoveFromCalendar),
            ImagePosition = NSCellImagePosition.ImageLeading,
            Hidden = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _invitationRemove.Activated += (_, _) => RunInvitationCommand(Invitation.RemoveFromCalendarCommand, null);

        var body = WinoLayout.VStack(10, statusBlock, factsCard, _invitationComment, _invitationRemove);
        body.DetachesHiddenViews = true;
        body.Alignment = NSLayoutAttribute.Leading;
        foreach (var row in new NSView[] { statusBlock, factsCard, _invitationComment })
            row.WidthAnchor.ConstraintEqualTo(body.WidthAnchor).Active = true;
        _invitationBody = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        WinoLayout.Fill(body, _invitationBody, 12);

        var root = WinoLayout.VStack(0, headerHost, _invitationErrorHost, _invitationBodySeparator, _invitationBody);
        root.DetachesHiddenViews = true;
        FillInvitationWidth(root);
        WinoLayout.Fill(root, card);

        Invitation.PropertyChanged += InvitationPropertyChanged;
        ApplyInvitation();
#if DEBUG
        MacDebugBridge.Register("reader-invitation", args => ReaderDebugAsync(() =>
        {
            if (args.Length > 0 && (args[0] == "expand") != _invitationExpanded) ToggleInvitation();
            var vm = Invitation;
            return $"visible={vm.IsVisible} cardHidden={_invitationCard.Hidden} expanded={_invitationExpanded} title=\"{vm.Title}\" icon={vm.HeaderIcon} " +
                   $"detail=\"{_invitationDetail.StringValue}\" canRespond={vm.CanRespond} busy={vm.IsBusy} accepted={vm.IsAcceptedResponse} " +
                   $"tentative={vm.IsTentativeResponse} declined={vm.IsDeclinedResponse} open={vm.CanOpenInCalendar} remove={vm.CanRemoveFromCalendar} " +
                   $"status=\"{vm.StatusText}\" reason=\"{vm.ReasonText}\" date=\"{vm.DateRangeText}\" location=\"{vm.Location}\" organizer=\"{vm.OrganizerText}\" " +
                   $"recurring={vm.IsRecurring} error=\"{vm.ErrorMessage}\"";
        }));
#endif
        return card;
    }

    private NSButton InvitationAnswerButton(string title, AttendeeStatus response)
    {
        var button = new NSButton
        {
            Title = title,
            BezelStyle = NSBezelStyle.Rounded,
            ControlSize = NSControlSize.Small,
            Font = WinoStyle.Caption,
            Image = WinoIcons.Image(AnswerGlyph(response), 12, AnswerTint(response)),
            ImagePosition = NSCellImagePosition.ImageLeading,
            Hidden = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoAccessibility.Label(button, title);
        button.ToolTip = title;
        button.SetContentCompressionResistancePriority(760, NSLayoutConstraintOrientation.Horizontal);
        button.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        button.Activated += (_, _) => RunInvitationCommand(Invitation.RespondCommand, response);
        return button;
    }

    /// <summary>
    /// Below the wide reader the answers keep only their glyphs (the title stays in the tooltip and for VoiceOver):
    /// with their titles the event title shrank to its first letter.
    /// </summary>
    private void SetInvitationAnswersCompact(bool compact)
    {
        foreach (var button in new[] { _invitationAccept, _invitationTentative, _invitationDecline })
        {
            if (button is null) continue;
            button.ImagePosition = compact ? NSCellImagePosition.ImageOnly : NSCellImagePosition.ImageLeading;
        }
    }

    private static WinoIconGlyph AnswerGlyph(AttendeeStatus response) => response switch
    {
        AttendeeStatus.Accepted => WinoIconGlyph.EventAccept,
        AttendeeStatus.Tentative => WinoIconGlyph.EventTentative,
        _ => WinoIconGlyph.EventDecline
    };

    private static NSColor AnswerTint(AttendeeStatus response) => response switch
    {
        AttendeeStatus.Accepted => WinoStyle.Success,
        AttendeeStatus.Tentative => WinoStyle.Caution,
        _ => WinoStyle.Critical
    };

    /// <summary>The answer's title, followed by a checkmark when it is the current answer (Windows shows the same mark).</summary>
    private static void ApplyAnswerTitle(NSButton button, string title, bool isCurrent)
    {
        var text = new NSMutableAttributedString(new NSAttributedString(title, new NSStringAttributes { Font = WinoStyle.Caption, ForegroundColor = NSColor.ControlText }));
        if (isCurrent && WinoIcons.Font(10) is { } iconFont)
        {
            text.Append(new NSAttributedString(" ", new NSStringAttributes { Font = WinoStyle.Caption }));
            text.Append(new NSAttributedString(WinoIcons.Glyph(WinoIconGlyph.Checkmark), new NSStringAttributes { Font = iconFont, ForegroundColor = NSColor.ControlText }));
        }
        button.AttributedTitle = text;
    }

    private static NSTextField WrappingLabel(NSFont font, NSColor color, string? text = null, bool selectable = false)
    {
        var label = WinoStyle.Label(text ?? string.Empty, font, color, 0);
        label.Alignment = NSTextAlignment.Left;
        label.Selectable = selectable;
        label.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        label.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        return label;
    }

    private static NSView FactRow(WinoIconGlyph glyph, NSTextField label)
    {
        var row = WinoLayout.HStack(10, new WinoIconView(glyph, 14, WinoStyle.PrimaryText), label);
        row.Alignment = NSLayoutAttribute.CenterY;
        return row;
    }

    /// <summary>Stretches every row of a vertical stack to its width (a leading stack with width constraints, as in the intelligence header).</summary>
    private static void FillInvitationWidth(NSStackView stack)
    {
        stack.Alignment = NSLayoutAttribute.Leading;
        foreach (var row in stack.ArrangedSubviews) row.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
    }

    private void ToggleInvitation()
    {
        if (_invitationDisposed) return;
        _invitationExpanded = !_invitationExpanded;
        ApplyInvitation();
    }

    private void RunInvitationCommand(CommunityToolkit.Mvvm.Input.IAsyncRelayCommand command, object? parameter)
    {
        if (_invitationDisposed || !command.CanExecute(parameter)) return;
        Observe(command.ExecuteAsync(parameter));
    }

    private void InvitationPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        // A hidden card is a new message (Windows unloads the card): the next one opens collapsed.
        if (args.PropertyName == nameof(MailInvitationCardViewModel.IsVisible) && sender is MailInvitationCardViewModel { IsVisible: false })
            _invitationCollapsePending = true;
        // The properties change one by one while an invitation loads; one update per run-loop turn.
        if (_invitationQueued) return;
        _invitationQueued = true;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
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
            _invitationSpinner.StopAnimation(null);
            return;
        }

        _invitationIcon.Icon = vm.HeaderIcon;
        _invitationTitle.StringValue = vm.Title ?? string.Empty;
        _invitationTitle.ToolTip = vm.Title;
        var detail = vm.CanRespond || string.IsNullOrEmpty(vm.StatusText) ? vm.HeaderSummaryText : vm.StatusText;
        _invitationDetail.StringValue = detail ?? string.Empty;
        _invitationDetail.ToolTip = detail;
        _invitationDetail.Hidden = vm.IsBusy;
        _invitationBusyRow.Hidden = !vm.IsBusy;
        if (vm.IsBusy) _invitationSpinner.StartAnimation(null);
        else _invitationSpinner.StopAnimation(null);

        foreach (var (button, title, isCurrent) in new[]
        {
            (_invitationAccept, Translator.CalendarEventRsvpPanel_Accept, vm.IsAcceptedResponse),
            (_invitationTentative, Translator.CalendarEventRsvpPanel_Tentative, vm.IsTentativeResponse),
            (_invitationDecline, Translator.CalendarEventRsvpPanel_Decline, vm.IsDeclinedResponse)
        })
        {
            button.Hidden = !vm.CanRespond;
            button.Enabled = vm.CanRespond && !vm.IsBusy;
            ApplyAnswerTitle(button, title, isCurrent);
        }
        _invitationOpen.Hidden = !vm.CanOpenInCalendar;
        _invitationOpen.Enabled = vm.CanOpenInCalendar && !vm.IsBusy;

        _invitationError.Message = vm.ErrorMessage;
        _invitationErrorHost.Hidden = !vm.HasError;

        var expanded = _invitationExpanded;
        _invitationChevron.Image = WinoIcons.Image(expanded ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown, 12);
        var expansion = expanded ? Translator.WinoIntelligence_Expanded : Translator.WinoIntelligence_Collapsed;
        WinoAccessibility.Label(_invitationChevron, $"{Translator.MailInvitation_CardAutomationName}, {expansion}");
        _invitationChevron.ToolTip = expansion;
        _invitationBodySeparator.Hidden = !expanded;
        _invitationBody.Hidden = !expanded;
        if (!expanded) return;

        _invitationStatus.StringValue = vm.StatusText ?? string.Empty;
        _invitationStatus.Hidden = !vm.HasStatus;
        _invitationReason.StringValue = vm.ReasonText ?? string.Empty;
        _invitationReason.Hidden = !vm.HasReason;
        _invitationStatusBlock.Hidden = !vm.HasStatus && !vm.HasReason;
        _invitationDate.StringValue = vm.DateRangeText ?? string.Empty;
        _invitationDateRow.Hidden = !vm.HasDateRange;
        _invitationRecurringRow.Hidden = !vm.IsRecurring;
        _invitationLocation.StringValue = vm.Location ?? string.Empty;
        _invitationLocationRow.Hidden = !vm.HasLocation;
        _invitationOrganizer.StringValue = vm.OrganizerText ?? string.Empty;
        _invitationOrganizerRow.Hidden = !vm.HasOrganizer;
        _invitationFactsCard.Hidden = !vm.HasDateRange && !vm.IsRecurring && !vm.HasLocation && !vm.HasOrganizer;

        _invitationComment.Hidden = !vm.CanRespond;
        _invitationComment.Enabled = vm.CanEditComment;
        if (_invitationComment.StringValue != (vm.Comment ?? string.Empty)) _invitationComment.StringValue = vm.Comment ?? string.Empty;
        _invitationRemove.Hidden = !vm.CanRemoveFromCalendar;
        _invitationRemove.Enabled = vm.CanRemoveFromCalendar && !vm.IsBusy;
    }

    /// <summary>The reader is leaving or being disposed. Safe to call more than once.</summary>
    private void InvitationDispose()
    {
        if (_invitationDisposed) return;
        _invitationDisposed = true;
        Invitation.PropertyChanged -= InvitationPropertyChanged;
        _invitationSpinner?.StopAnimation(null);
    }

    /// <summary>The invitation header row: the whole row lights up on hover, not only the toggle, as on Windows.</summary>
    private sealed class InvitationHeaderRow : WinoSurfaceView
    {
        public InvitationHeaderRow()
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            AddTrackingArea(new NSTrackingArea(default,
                NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInActiveApp | NSTrackingAreaOptions.InVisibleRect, this, null));
        }

        public override void MouseEntered(NSEvent theEvent) => Fill = WinoStyle.RowHoverFill;

        public override void MouseExited(NSEvent theEvent) => Fill = null;
    }

    /// <summary>
    /// A narrow reader leaves the header's date summary a few letters wide ("10…"); it fades out instead, and the
    /// expanded details still show it in full. Alpha keeps its layout slot, so the measurement does not flip back.
    /// </summary>
    private void FitInvitationDetail()
    {
        if (_invitationDetail is null || _invitationDetail.Hidden) return;
        double needed = Math.Min(_invitationDetail.IntrinsicContentSize.Width, 96);
        nfloat alpha = _invitationDetail.Frame.Width + 0.5 >= needed ? 1 : 0;
        if (_invitationDetail.AlphaValue != alpha) _invitationDetail.AlphaValue = alpha;
    }
}
