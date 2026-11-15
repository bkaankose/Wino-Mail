using System.ComponentModel;
using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.TextField;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Mail;

/// <summary>
/// The calendar invitation card above the message body (Windows MailInvitationCard) over
/// <see cref="MailRenderingPageViewModel.Invitation"/>.
/// </summary>
public sealed partial class MailReaderFragment
{
    private View? _invitationCard;
    private View? _invitationHeader;
    private WinoIconView? _invitationIcon;
    private WinoIconView? _invitationChevron;
    private TextView? _invitationTitle;
    private TextView? _invitationDetail;
    private CircularProgressIndicator? _invitationBusy;
    private View? _invitationResponses;
    private MaterialButton? _invitationAccept;
    private MaterialButton? _invitationTentative;
    private MaterialButton? _invitationDecline;
    private MaterialButton? _invitationOpen;
    private WinoInfoBar? _invitationError;
    private View? _invitationBody;
    private TextView? _invitationStatus;
    private TextView? _invitationReason;
    private LinearLayout? _invitationDetails;
    private TextInputLayout? _invitationCommentLayout;
    private TextInputEditText? _invitationComment;
    private MaterialButton? _invitationRemove;
    private bool _invitationExpanded;
    private bool _invitationWasVisible;

    private void BuildInvitation(View view, Context context, MailRenderingPageViewModel viewModel, IDispatcher dispatcher)
    {
        _invitationCard = view.FindViewById(Resource.Id.reader_invitation_card);
        if (_invitationCard is null) return;
        _invitationHeader = view.FindViewById(Resource.Id.invitation_header);
        _invitationIcon = view.FindViewById<WinoIconView>(Resource.Id.invitation_icon);
        _invitationChevron = view.FindViewById<WinoIconView>(Resource.Id.invitation_chevron);
        _invitationTitle = view.FindViewById<TextView>(Resource.Id.invitation_title);
        _invitationDetail = view.FindViewById<TextView>(Resource.Id.invitation_detail);
        _invitationBusy = view.FindViewById<CircularProgressIndicator>(Resource.Id.invitation_busy);
        _invitationResponses = view.FindViewById(Resource.Id.invitation_responses);
        _invitationAccept = view.FindViewById<MaterialButton>(Resource.Id.invitation_accept);
        _invitationTentative = view.FindViewById<MaterialButton>(Resource.Id.invitation_tentative);
        _invitationDecline = view.FindViewById<MaterialButton>(Resource.Id.invitation_decline);
        _invitationOpen = view.FindViewById<MaterialButton>(Resource.Id.invitation_open);
        _invitationError = view.FindViewById<WinoInfoBar>(Resource.Id.invitation_error);
        _invitationBody = view.FindViewById(Resource.Id.invitation_body);
        _invitationStatus = view.FindViewById<TextView>(Resource.Id.invitation_status);
        _invitationReason = view.FindViewById<TextView>(Resource.Id.invitation_reason);
        _invitationDetails = view.FindViewById<LinearLayout>(Resource.Id.invitation_details);
        _invitationCommentLayout = view.FindViewById<TextInputLayout>(Resource.Id.invitation_comment_layout);
        _invitationComment = view.FindViewById<TextInputEditText>(Resource.Id.invitation_comment);
        _invitationRemove = view.FindViewById<MaterialButton>(Resource.Id.invitation_remove);

        var invitation = viewModel.Invitation;

        if (_invitationIcon is { } icon)
        {
            icon.GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_m);
            icon.SetTextColor(WinoColors.AccentText(context));
        }
        if (_invitationChevron is { } chevron)
        {
            chevron.Icon = WinoIconGlyph.ChevronDown;
            chevron.GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_2xs);
        }

        ConfigureResponse(context, _invitationAccept, Translator.CalendarEventRsvpPanel_Accept, WinoIconGlyph.EventAccept, WinoColors.Success(context), AttendeeStatus.Accepted, invitation);
        ConfigureResponse(context, _invitationTentative, Translator.CalendarEventRsvpPanel_Tentative, WinoIconGlyph.EventTentative, WinoColors.Caution(context), AttendeeStatus.Tentative, invitation);
        ConfigureResponse(context, _invitationDecline, Translator.CalendarEventRsvpPanel_Decline, WinoIconGlyph.EventDecline, WinoColors.Critical(context), AttendeeStatus.Declined, invitation);

        if (_invitationOpen is { } open)
        {
            open.Text = Translator.MailInvitation_OpenInCalendar;
            BindCommand(open, invitation.OpenInCalendarCommand, dispatcher);
        }
        if (_invitationRemove is { } remove)
        {
            remove.Text = Translator.MailInvitation_RemoveFromCalendar;
            remove.Icon = WinoIcons.Drawable(context, WinoIconGlyph.Delete, WinoTokens.Dp(context, Resource.Dimension.wino_glyph_m), WinoColors.AccentText(context));
            remove.IconTint = null;
            BindCommand(remove, invitation.RemoveFromCalendarCommand, dispatcher);
        }
        if (_invitationError is { } error)
        {
            error.Severity = WinoInfoBarSeverity.Error;
            error.Title = Translator.MailInvitation_ActionFailedTitle;
            error.IsClosable = false;
        }
        if (_invitationCommentLayout is { } commentLayout)
            commentLayout.Hint = Translator.CalendarEventRsvpPanel_AddMessage;
        if (_invitationComment is { } comment)
        {
            comment.ContentDescription = Translator.CalendarEventRsvpPanel_Message;
            EventHandler<TextChangedEventArgs> commentChanged = (_, _) =>
            {
                var text = comment.Text ?? string.Empty;
                if (!string.Equals(text, invitation.Comment, StringComparison.Ordinal)) invitation.Comment = text;
            };
            comment.TextChanged += commentChanged;
            Bindings.Own(new ActionDisposable(() => comment.TextChanged -= commentChanged));
        }
        if (_invitationHeader is { } header)
        {
            EventHandler toggled = (_, _) => ToggleInvitation();
            header.Click += toggled;
            Bindings.Own(new ActionDisposable(() => header.Click -= toggled));
        }

        PropertyChangedEventHandler changed = (_, _) => _ = dispatcher.ExecuteOnUIThread(SyncInvitation);
        invitation.PropertyChanged += changed;
        Bindings.Own(new ActionDisposable(() =>
        {
            invitation.PropertyChanged -= changed;
            _invitationCard = null;
        }));

        _invitationExpanded = false;
        _invitationWasVisible = invitation.IsVisible;
        SyncInvitation();
    }

    private void ConfigureResponse(Context context, MaterialButton? button, string text, WinoIconGlyph glyph, Color tint, AttendeeStatus response, MailInvitationCardViewModel invitation)
    {
        if (button is null) return;
        button.Text = text;
        button.Icon = WinoIcons.Drawable(context, glyph, WinoTokens.Dp(context, Resource.Dimension.wino_glyph_s), tint);
        button.IconTint = null;
        EventHandler clicked = (_, _) =>
        {
            // A click toggles the checkable button; the ViewModel decides which answer is current.
            ApplyResponseChecks(invitation);
            if (invitation.RespondCommand.CanExecute(response)) Observe(invitation.RespondCommand.ExecuteAsync(response));
        };
        button.Click += clicked;
        Bindings.Own(new ActionDisposable(() => button.Click -= clicked));
    }

    private void ToggleInvitation()
    {
        _invitationExpanded = !_invitationExpanded;
        if (_invitationChevron is { } chevron)
            chevron.Animate()?.Rotation(_invitationExpanded ? 180f : 0f).SetDuration(167).Start();
        SyncInvitation();
    }

    private void ApplyResponseChecks(MailInvitationCardViewModel invitation)
    {
        if (_invitationAccept is not null) _invitationAccept.Checked = invitation.IsAcceptedResponse;
        if (_invitationTentative is not null) _invitationTentative.Checked = invitation.IsTentativeResponse;
        if (_invitationDecline is not null) _invitationDecline.Checked = invitation.IsDeclinedResponse;
    }

    private void SyncInvitation()
    {
        if (_invitationCard is not { } card || ViewModel?.Invitation is not { } invitation || card.Context is not { } context) return;

        if (!invitation.IsVisible)
        {
            card.Visibility = ViewStates.Gone;
            _invitationWasVisible = false;
            return;
        }
        if (!_invitationWasVisible)
        {
            // A new invitation starts collapsed, as the Windows card does when it loads again.
            _invitationWasVisible = true;
            _invitationExpanded = false;
            if (_invitationChevron is not null) _invitationChevron.Rotation = 0f;
        }
        card.Visibility = ViewStates.Visible;

        if (_invitationIcon is not null) _invitationIcon.Icon = invitation.HeaderIcon;
        if (_invitationTitle is not null) _invitationTitle.Text = invitation.Title;
        var detail = invitation.CanRespond || string.IsNullOrEmpty(invitation.StatusText) ? invitation.HeaderSummaryText : invitation.StatusText;
        if (_invitationDetail is not null)
        {
            _invitationDetail.Text = invitation.IsBusy ? Translator.MailInvitation_Busy : detail;
            _invitationDetail.Visibility = string.IsNullOrEmpty(_invitationDetail.Text) ? ViewStates.Gone : ViewStates.Visible;
        }
        if (_invitationBusy is not null) _invitationBusy.Visibility = invitation.IsBusy ? ViewStates.Visible : ViewStates.Gone;
        if (_invitationHeader is not null)
            _invitationHeader.ContentDescription = string.Join(", ", new[]
            {
                Translator.MailInvitation_CardAutomationName,
                invitation.Title,
                invitation.IsBusy ? Translator.MailInvitation_Busy : detail,
                _invitationExpanded ? Translator.WinoIntelligence_Expanded : Translator.WinoIntelligence_Collapsed
            }.Where(static part => !string.IsNullOrEmpty(part)));

        if (_invitationResponses is not null) _invitationResponses.Visibility = invitation.CanRespond ? ViewStates.Visible : ViewStates.Gone;
        bool canAnswer = invitation.CanRespond && !invitation.IsBusy;
        foreach (var button in new[] { _invitationAccept, _invitationTentative, _invitationDecline })
            if (button is not null) button.Enabled = canAnswer;
        ApplyResponseChecks(invitation);
        if (_invitationOpen is not null) _invitationOpen.Visibility = invitation.CanOpenInCalendar ? ViewStates.Visible : ViewStates.Gone;

        if (_invitationError is not null)
        {
            _invitationError.Message = invitation.ErrorMessage;
            _invitationError.Visibility = invitation.HasError ? ViewStates.Visible : ViewStates.Gone;
        }

        if (_invitationBody is not null) _invitationBody.Visibility = _invitationExpanded ? ViewStates.Visible : ViewStates.Gone;
        if (!_invitationExpanded) return;

        SetInvitationText(_invitationStatus, invitation.StatusText);
        SetInvitationText(_invitationReason, invitation.ReasonText);
        if (_invitationDetails is { } details)
        {
            details.RemoveAllViews();
            if (invitation.HasDateRange) AddInvitationDetail(context, details, WinoIconGlyph.Clock, invitation.DateRangeText);
            if (invitation.IsRecurring) AddInvitationDetail(context, details, WinoIconGlyph.CalendarEventRepeat, Translator.MailInvitation_Recurring);
            if (invitation.HasLocation) AddInvitationDetail(context, details, WinoIconGlyph.Location, invitation.Location);
            if (invitation.HasOrganizer) AddInvitationDetail(context, details, WinoIconGlyph.Person, invitation.OrganizerText);
            details.Visibility = details.ChildCount == 0 ? ViewStates.Gone : ViewStates.Visible;
        }
        if (_invitationCommentLayout is not null)
        {
            _invitationCommentLayout.Visibility = invitation.CanRespond ? ViewStates.Visible : ViewStates.Gone;
            _invitationCommentLayout.Enabled = invitation.CanEditComment;
        }
        if (_invitationComment is { } comment && !string.Equals(comment.Text ?? string.Empty, invitation.Comment, StringComparison.Ordinal))
            comment.Text = invitation.Comment;
        if (_invitationRemove is not null) _invitationRemove.Visibility = invitation.CanRemoveFromCalendar ? ViewStates.Visible : ViewStates.Gone;
    }

    private static void SetInvitationText(TextView? view, string text)
    {
        if (view is null) return;
        view.Text = text;
        view.Visibility = string.IsNullOrEmpty(text) ? ViewStates.Gone : ViewStates.Visible;
    }

    private static void AddInvitationDetail(Context context, LinearLayout details, WinoIconGlyph glyph, string text)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        int vertical = WinoTokens.Px(context, Resource.Dimension.wino_space_2xs);
        row.SetPadding(0, vertical, 0, vertical);
        var icon = new WinoIconView(context) { Icon = glyph, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_s) };
        icon.SetTextColor(WinoColors.PrimaryText(context));
        row.AddView(icon, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        var label = new TextView(context) { Text = text };
        label.SetTextAppearance(Resource.Style.TextAppearance_WinoKit_Body);
        label.SetTextIsSelectable(true);
        row.AddView(label, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
        {
            MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_sm)
        });
        details.AddView(row);
    }
}
