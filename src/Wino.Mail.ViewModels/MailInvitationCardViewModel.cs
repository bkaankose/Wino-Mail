#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MimeKit;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Helpers;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The invitation card of the reader: the event an iMIP message carries, the user's answer and the actions on it.
/// The invitation is read from the MIME, so mail stored before invitations were recognized shows the card too.
/// </summary>
public partial class MailInvitationCardViewModel : ObservableObject
{
    private readonly IMailInvitationService? _invitationService;
    private readonly ICalendarService _calendarService;
    private readonly INavigationService _navigationService;
    private readonly IPreferencesService _preferencesService;
    private readonly Func<Action, Task> _executeOnUIThread;

    private int _version;
    private MailCopy? _mailCopy;
    private MimeMessage? _message;
    private MailInvitationState? _state;

    public MailInvitationCardViewModel(IMailInvitationService? invitationService,
                                       ICalendarService calendarService,
                                       INavigationService navigationService,
                                       IPreferencesService preferencesService,
                                       Func<Action, Task> executeOnUIThread)
    {
        _invitationService = invitationService;
        _calendarService = calendarService;
        _navigationService = navigationService;
        _preferencesService = preferencesService;
        _executeOnUIThread = executeOnUIThread;
    }

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial WinoIconGlyph HeaderIcon { get; set; } = WinoIconGlyph.Calendar;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDateRange))]
    public partial string DateRangeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRecurring { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocation))]
    public partial string Location { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOrganizer))]
    public partial string OrganizerText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>Why the card cannot offer answers, from the service.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReason))]
    public partial string ReasonText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditComment))]
    [NotifyCanExecuteChangedFor(nameof(RespondCommand))]
    public partial bool CanRespond { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenInCalendarCommand))]
    public partial bool CanOpenInCalendar { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveFromCalendarCommand))]
    public partial bool CanRemoveFromCalendar { get; set; }

    [ObservableProperty]
    public partial bool IsAcceptedResponse { get; set; }

    [ObservableProperty]
    public partial bool IsTentativeResponse { get; set; }

    [ObservableProperty]
    public partial bool IsDeclinedResponse { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditComment))]
    [NotifyCanExecuteChangedFor(nameof(RespondCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveFromCalendarCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenInCalendarCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Comment { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public bool HasDateRange => !string.IsNullOrEmpty(DateRangeText);
    public bool HasLocation => !string.IsNullOrEmpty(Location);
    public bool HasOrganizer => !string.IsNullOrEmpty(OrganizerText);
    public bool HasStatus => !string.IsNullOrEmpty(StatusText);
    public bool HasReason => !string.IsNullOrEmpty(ReasonText);
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool CanEditComment => CanRespond && !IsBusy;

    /// <summary>Hides the card and drops the previous message. Results of loads and answers still running are ignored.</summary>
    public void Reset()
    {
        Interlocked.Increment(ref _version);

        _mailCopy = null;
        _message = null;
        _state = null;

        IsBusy = false;
        Comment = string.Empty;
        ErrorMessage = string.Empty;
        ApplyState(null);
    }

    public async Task LoadAsync(MailCopy mailCopy, MimeMessage message, CancellationToken cancellationToken, Action? onLoaded = null)
    {
        var version = Volatile.Read(ref _version);

        if (_invitationService == null)
            return;

        MailInvitationState? state;

        try
        {
            state = await _invitationService.GetStateAsync(mailCopy, message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read the invitation of mail {MailUniqueId}.", mailCopy.UniqueId);
            return;
        }

        if (state == null || cancellationToken.IsCancellationRequested)
            return;

        await _executeOnUIThread(() =>
        {
            if (version != Volatile.Read(ref _version))
                return;

            _mailCopy = mailCopy;
            _message = message;
            ErrorMessage = string.Empty;
            ApplyState(state);
            onLoaded?.Invoke();
        }).ConfigureAwait(false);
    }

    private bool CanRespondNow(AttendeeStatus response) => CanRespond && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRespondNow))]
    private Task RespondAsync(AttendeeStatus response)
    {
        var comment = string.IsNullOrWhiteSpace(Comment) ? null : Comment.Trim();

        return RunAsync((service, mailCopy, message) => service.RespondAsync(mailCopy, message, response, comment), clearComment: true);
    }

    private bool CanRemoveNow() => CanRemoveFromCalendar && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRemoveNow))]
    private Task RemoveFromCalendarAsync()
        => RunAsync((service, mailCopy, message) => service.RemoveCancelledEventAsync(mailCopy, message), clearComment: false);

    private bool CanOpenNow() => CanOpenInCalendar && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanOpenNow))]
    private async Task OpenInCalendarAsync()
    {
        if (_state?.CalendarItemId is not Guid calendarItemId)
            return;

        var version = Volatile.Read(ref _version);

        try
        {
            var calendarItem = await _calendarService.GetCalendarItemAsync(calendarItemId).ConfigureAwait(false);

            await _executeOnUIThread(() =>
            {
                if (version != Volatile.Read(ref _version))
                    return;

                if (calendarItem == null)
                {
                    ErrorMessage = Translator.MailInvitation_EventNotFound;
                    return;
                }

                var navigationArgs = new CalendarPageNavigationArgs
                {
                    NavigationDate = calendarItem.LocalStartDate,
                    PendingTarget = new CalendarItemTarget(calendarItem, CalendarEventTargetType.Single)
                };

                _navigationService.ChangeApplicationMode(WinoApplicationMode.Calendar, new ShellModeActivationContext { Parameter = navigationArgs });
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open invitation event {CalendarItemId} in the calendar.", calendarItemId);
            await _executeOnUIThread(() => ErrorMessage = ex.Message).ConfigureAwait(false);
        }
    }

    /// <remarks>
    /// The server call is never cancelled: once sent, an answer must finish so local data follows the server.
    /// A newer message in the reader only makes the result irrelevant to this card.
    /// </remarks>
    private async Task RunAsync(Func<IMailInvitationService, MailCopy, MimeMessage, Task<MailInvitationState>> operation, bool clearComment)
    {
        if (_invitationService == null || _mailCopy == null || _message == null)
            return;

        var version = Volatile.Read(ref _version);
        var mailCopy = _mailCopy;
        var message = _message;

        await _executeOnUIThread(() =>
        {
            IsBusy = true;
            ErrorMessage = string.Empty;
        }).ConfigureAwait(false);

        try
        {
            var state = await operation(_invitationService, mailCopy, message).ConfigureAwait(false);

            await _executeOnUIThread(() =>
            {
                if (version != Volatile.Read(ref _version))
                    return;

                if (clearComment)
                    Comment = string.Empty;

                ApplyState(state);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Invitation action failed for mail {MailUniqueId}.", mailCopy.UniqueId);

            await _executeOnUIThread(() =>
            {
                if (version == Volatile.Read(ref _version))
                    ErrorMessage = ex.Message;
            }).ConfigureAwait(false);
        }
        finally
        {
            await _executeOnUIThread(() =>
            {
                if (version == Volatile.Read(ref _version))
                    IsBusy = false;
            }).ConfigureAwait(false);
        }
    }

    private void ApplyState(MailInvitationState? state)
    {
        _state = state;

        if (state == null)
        {
            IsVisible = false;
            Title = string.Empty;
            HeaderIcon = WinoIconGlyph.Calendar;
            DateRangeText = string.Empty;
            IsRecurring = false;
            Location = string.Empty;
            OrganizerText = string.Empty;
            StatusText = string.Empty;
            ReasonText = string.Empty;
            CanRespond = false;
            CanOpenInCalendar = false;
            CanRemoveFromCalendar = false;
            IsAcceptedResponse = false;
            IsTentativeResponse = false;
            IsDeclinedResponse = false;
            return;
        }

        var invitation = state.Invitation;

        Title = string.IsNullOrWhiteSpace(invitation.Summary) ? Translator.QuickAction_UntitledEvent : invitation.Summary.Trim();
        HeaderIcon = state.IsCancelled ? WinoIconGlyph.CalendarCancel : WinoIconGlyph.Calendar;
        DateRangeText = FormatDateRange(invitation);
        IsRecurring = invitation.IsRecurring;
        Location = invitation.Location.Trim();
        OrganizerText = GetOrganizerText(invitation);
        StatusText = GetStatusText(state);
        ReasonText = string.Equals(state.UnavailableReason, StatusText, StringComparison.Ordinal) ? string.Empty : state.UnavailableReason;
        CanRespond = state.CanRespond;
        CanOpenInCalendar = state.IsInCalendar;
        CanRemoveFromCalendar = state.CanRemoveFromCalendar;
        IsAcceptedResponse = state.CurrentResponse == AttendeeStatus.Accepted;
        IsTentativeResponse = state.CurrentResponse == AttendeeStatus.Tentative;
        IsDeclinedResponse = state.CurrentResponse == AttendeeStatus.Declined;
        IsVisible = true;
    }

    private string FormatDateRange(MailInvitation invitation)
        => EventDateRangeFormatter.Format(invitation, _preferencesService.MailTimeFormatPreference, EventDateRangeFormatter.DisplayCulture);

    private static string GetOrganizerText(MailInvitation invitation)
    {
        var name = invitation.OrganizerName.Trim();
        var email = invitation.OrganizerEmail.Trim();

        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(email))
            return string.Empty;

        var display = string.IsNullOrEmpty(name) || string.Equals(name, email, StringComparison.OrdinalIgnoreCase)
            ? email
            : string.IsNullOrEmpty(email) ? name : $"{name} <{email}>";

        return string.Format(Translator.MailInvitation_OrganizedBy, display);
    }

    private static string GetStatusText(MailInvitationState state)
    {
        if (state.IsCancelled)
            return Translator.MailInvitation_StatusCancelled;

        if (state.IsOutdated)
            return Translator.MailInvitation_StatusOutdated;

        if (state.Invitation.Method == MailInvitationMethod.Reply)
            return GetReplyStatusText(state.Invitation);

        if (state.IsOrganizer)
            return Translator.MailInvitation_StatusOrganizer;

        if (state.IsUpdate)
            return Translator.MailInvitation_StatusUpdated;

        return state.CurrentResponse switch
        {
            AttendeeStatus.Accepted => Translator.MailInvitation_StatusAccepted,
            AttendeeStatus.Tentative => Translator.MailInvitation_StatusTentative,
            AttendeeStatus.Declined => Translator.MailInvitation_StatusDeclined,
            AttendeeStatus.NeedsAction => Translator.MailInvitation_StatusNotAnswered,
            _ => Translator.MailInvitation_StatusForwarded
        };
    }

    private static string GetReplyStatusText(MailInvitation invitation)
    {
        var attendee = invitation.Attendees.FirstOrDefault();

        if (attendee == null)
            return string.Empty;

        var name = string.IsNullOrWhiteSpace(attendee.Name) ? attendee.Email : attendee.Name;

        var format = attendee.Status switch
        {
            AttendeeStatus.Accepted => Translator.MailInvitation_ReplyAccepted,
            AttendeeStatus.Tentative => Translator.MailInvitation_ReplyTentative,
            AttendeeStatus.Declined => Translator.MailInvitation_ReplyDeclined,
            _ => string.Empty
        };

        return string.IsNullOrEmpty(format) || string.IsNullOrWhiteSpace(name) ? string.Empty : string.Format(format, name);
    }
}
