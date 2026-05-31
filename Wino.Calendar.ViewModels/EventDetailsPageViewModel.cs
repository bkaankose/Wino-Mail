using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using EmailValidation;
using Serilog;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Services;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.ViewModels;

public partial class EventDetailsPageViewModel : CalendarBaseViewModel
{
    private readonly ICalendarService _calendarService;
    private readonly INativeAppService _nativeAppService;
    private readonly IPreferencesService _preferencesService;
    private readonly IMailDialogService _dialogService;
    private readonly IWinoRequestDelegator _winoRequestDelegator;
    private readonly INavigationService _navigationService;
    private readonly IUnderlyingThemeService _underlyingThemeService;
    private readonly INotificationBuilder _notificationBuilder;
    private readonly IContactService _contactService;

    public CalendarSettings CurrentSettings { get; }
    public INativeAppService NativeAppService => _nativeAppService;
    public Func<Task<string>> GetHtmlNotesAsync { get; set; }
    public string TimePickerClockIdentifier => CurrentSettings.DayHeaderDisplayType == DayHeaderDisplayType.TwentyFourHour ? "24HourClock" : "12HourClock";

    [ObservableProperty]
    public partial bool IsDarkWebviewRenderer { get; set; }

    public ObservableCollection<CalendarAttachmentViewModel> Attachments { get; } = new ObservableCollection<CalendarAttachmentViewModel>();

    /// <summary>
    /// Returns true if the current event has attachments.
    /// </summary>
    public bool HasAttachments => Attachments.Count > 0;

    public ObservableCollection<CalendarComposeAttendeeViewModel> EditableAttendees { get; } = [];

    #region Details

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanViewSeries))]
    [NotifyPropertyChangedFor(nameof(CanEditSeries))]
    [NotifyPropertyChangedFor(nameof(IsCurrentUserOrganizer))]
    [NotifyPropertyChangedFor(nameof(CanEditEventDetails))]
    [NotifyPropertyChangedFor(nameof(IsEventDetailsReadOnly))]
    [NotifyPropertyChangedFor(nameof(IsTimedEditorVisible))]
    [NotifyPropertyChangedFor(nameof(IsAllDayEndDateEditorVisible))]
    [NotifyPropertyChangedFor(nameof(CanEditPersonalOptions))]
    [NotifyPropertyChangedFor(nameof(CanRespond))]
    [NotifyPropertyChangedFor(nameof(CanDeleteEvent))]
    [NotifyPropertyChangedFor(nameof(CurrentRsvpText))]
    [NotifyPropertyChangedFor(nameof(CurrentRsvpStatus))]
    public partial CalendarItemViewModel CurrentEvent { get; set; }

    partial void OnCurrentEventChanged(CalendarItemViewModel value)
    {
        // Notify the view to re-render the description
        Messenger.Send(new CalendarDescriptionRenderingRequested());
    }

    [ObservableProperty]
    public partial CalendarItemViewModel SeriesParent { get; set; }
    [ObservableProperty]
    public partial List<Reminder> Reminders { get; set; }

    public ObservableCollection<ReminderOption> ReminderOptions { get; } = new ObservableCollection<ReminderOption>();

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Location { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimedEditorVisible))]
    [NotifyPropertyChangedFor(nameof(IsAllDayEndDateEditorVisible))]
    public partial bool IsAllDay { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset StartDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan StartTime { get; set; }

    [ObservableProperty]
    public partial TimeSpan EndTime { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset AllDayEndDate { get; set; }

    [ObservableProperty]
    public partial string RecurrenceSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ReminderOption SelectedReminderOption { get; set; }

    /// <summary>
    /// Returns true if the event is part of a recurring series (as a child occurrence).
    /// Used to enable "View Series" functionality.
    /// </summary>
    public bool CanViewSeries => CurrentEvent?.IsRecurringChild ?? false;

    /// <summary>
    /// Returns true if the "Edit Series" button should be visible.
    /// Only visible for child occurrences of recurring events, not for master events or single events.
    /// </summary>
    public bool CanEditSeries => CurrentEvent?.IsRecurringChild ?? false;

    /// <summary>
    /// Returns true if the current user is the organizer of the event.
    /// Used to determine if the user can invite attendees or modify the event.
    /// </summary>
    private CalendarEventEditPolicy CurrentEditPolicy => CalendarEventEditPolicy.From(CurrentEvent?.CalendarItem);

    public bool IsCurrentUserOrganizer => CurrentEditPolicy.IsCurrentUserOrganizer;
    public bool CanEditEventDetails => CurrentEditPolicy.CanEditEventDetails;
    public bool IsEventDetailsReadOnly => !CanEditEventDetails;
    public bool IsTimedEditorVisible => CanEditEventDetails && !IsAllDay;
    public bool IsAllDayEndDateEditorVisible => CanEditEventDetails && IsAllDay;
    public bool CanEditPersonalOptions => CurrentEditPolicy.CanEditPersonalOptions;
    public bool CanRespond => CurrentEditPolicy.CanRespond;
    public bool CanDeleteEvent => CurrentEditPolicy.CanDeleteEvent;

    #endregion

    #region Show As Options

    public ObservableCollection<ShowAsOption> ShowAsOptions { get; } = new ObservableCollection<ShowAsOption>();

    [ObservableProperty]
    public partial ShowAsOption SelectedShowAsOption { get; set; }

    #endregion

    #region RSVP Panel

    [ObservableProperty]
    public partial bool IsRsvpPanelVisible { get; set; }

    public bool IncludeRsvpMessage => !string.IsNullOrEmpty(RsvpMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IncludeRsvpMessage))]
    public partial string RsvpMessage { get; set; } = string.Empty;

    public ObservableCollection<RsvpStatusOption> RsvpStatusOptions { get; } = new ObservableCollection<RsvpStatusOption>();

    public CalendarItemStatus CurrentRsvpStatus
    {
        get
        {
            return CurrentEvent?.CalendarItem?.Status ?? CalendarItemStatus.NotResponded;
        }
    }

    public string CurrentRsvpText
    {
        get
        {
            if (CurrentEvent?.CalendarItem == null) return Translator.CalendarEventResponse_Accept;

            return CurrentEvent.CalendarItem.Status switch
            {
                CalendarItemStatus.Accepted => Translator.CalendarEventResponse_AcceptedResponse,
                CalendarItemStatus.Tentative => Translator.CalendarEventResponse_TentativeResponse,
                CalendarItemStatus.Cancelled => Translator.CalendarEventResponse_DeclinedResponse,
                CalendarItemStatus.NotResponded => Translator.CalendarEventResponse_NotResponded,
                _ => Translator.CalendarEventResponse_NotResponded
            };
        }
    }

    #endregion

    public EventDetailsPageViewModel(ICalendarService calendarService,
                                     INativeAppService nativeAppService,
                                     IPreferencesService preferencesService,
                                     IMailDialogService dialogService,
                                     IWinoRequestDelegator winoRequestDelegator,
                                     INavigationService navigationService,
                                     INotificationBuilder notificationBuilder,
                                     IUnderlyingThemeService underlyingThemeService,
                                     IContactService contactService)
    {
        _calendarService = calendarService;
        _nativeAppService = nativeAppService;
        _preferencesService = preferencesService;
        _dialogService = dialogService;
        _winoRequestDelegator = winoRequestDelegator;
        _navigationService = navigationService;
        _underlyingThemeService = underlyingThemeService;
        _notificationBuilder = notificationBuilder;
        _contactService = contactService;

        CurrentSettings = _preferencesService.GetCurrentCalendarSettings();
        IsDarkWebviewRenderer = _underlyingThemeService.IsUnderlyingThemeDark();

        foreach (var showAs in CalendarItemActionOptions.ShowAsOptions)
        {
            ShowAsOptions.Add(new ShowAsOption(showAs));
        }

        SelectedShowAsOption = ShowAsOptions.FirstOrDefault(option => option.ShowAs == CalendarItemShowAs.Busy) ?? ShowAsOptions.FirstOrDefault();

        foreach (var responseStatus in CalendarItemActionOptions.ResponseOptions)
        {
            RsvpStatusOptions.Add(new RsvpStatusOption(responseStatus));
        }
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (parameters == null || parameters is not CalendarItemTarget args)
            return;

        await LoadCalendarItemTargetAsync(args);
    }

    protected override async void OnCalendarItemUpdated(CalendarItem calendarItem, EntityUpdateSource source)
    {
        base.OnCalendarItemUpdated(calendarItem, source);

        // If the current event was updated, reload it
        if (IsCurrentEventMatch(calendarItem))
        {
            // Reflect client-side optimistic changes immediately; fallback to DB for server updates.
            if (source == EntityUpdateSource.ClientUpdated || source == EntityUpdateSource.ClientReverted)
            {
                var previousAttendees = CurrentEvent?.Attendees?.ToList() ?? [];
                CurrentEvent = new CalendarItemViewModel(calendarItem)
                {
                    IsBusy = source == EntityUpdateSource.ClientUpdated
                };

                foreach (var attendee in previousAttendees)
                {
                    CurrentEvent.Attendees.Add(attendee);
                }

                return;
            }

            // Refresh from DB when update comes from server sync.
            var refreshedEvent = await _calendarService.GetCalendarItemAsync(calendarItem.Id);
            if (refreshedEvent != null)
            {
                CurrentEvent = new CalendarItemViewModel(refreshedEvent);
                await LoadAttendeesAsync(refreshedEvent.Id, refreshedEvent);
            }
        }
    }

    protected override async void OnCalendarItemAdded(CalendarItem calendarItem, EntityUpdateSource source)
    {
        base.OnCalendarItemAdded(calendarItem, source);

        if (!IsCurrentEventMatch(calendarItem))
            return;

        if (source == EntityUpdateSource.ClientUpdated || source == EntityUpdateSource.ClientReverted)
        {
            CurrentEvent = new CalendarItemViewModel(calendarItem)
            {
                IsBusy = source == EntityUpdateSource.ClientUpdated
            };

            return;
        }

        var refreshedEvent = await _calendarService.GetCalendarItemAsync(calendarItem.Id);
        if (refreshedEvent != null)
        {
            CurrentEvent = new CalendarItemViewModel(refreshedEvent);
            await LoadAttendeesAsync(refreshedEvent.Id, refreshedEvent);
        }
    }

    protected override void OnCalendarItemDeleted(CalendarItem calendarItem, EntityUpdateSource source)
    {
        base.OnCalendarItemDeleted(calendarItem, source);

        // If the current event was deleted, navigate back
        if (IsCurrentEventMatch(calendarItem))
        {
            NavigateBackToCalendar(forceReload: true);
        }
    }

    private bool IsCurrentEventMatch(CalendarItem calendarItem)
    {
        if (CurrentEvent?.CalendarItem == null || calendarItem == null)
            return false;

        var trackedLocalItemId = calendarItem.RemoteEventId.GetClientTrackingId();

        return CurrentEvent.CalendarItem.Id == calendarItem.Id ||
               (trackedLocalItemId.HasValue && CurrentEvent.CalendarItem.Id == trackedLocalItemId.Value) ||
               CurrentEvent.CalendarItem.RecurringCalendarItemId == calendarItem.Id;
    }

    private async Task LoadCalendarItemTargetAsync(CalendarItemTarget target)
    {
        try
        {
            var currentEventItem = await _calendarService.GetCalendarItemTargetAsync(target);

            if (currentEventItem == null)
                return;

            CurrentEvent = new CalendarItemViewModel(currentEventItem);

            await LoadAttendeesAsync(currentEventItem.Id, currentEventItem);

            // Initialize SelectedShowAsOption based on current event's ShowAs
            SelectedShowAsOption = ShowAsOptions.FirstOrDefault(o => o.ShowAs == currentEventItem.ShowAs) ?? ShowAsOptions[2];

            // Load reminders for this calendar item
            Reminders = await _calendarService.GetRemindersAsync(currentEventItem.Id);
            InitializeReminderOptions();
            InitializeDraft(currentEventItem);

            // Load attachments
            await LoadAttachmentsAsync(currentEventItem.Id);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
    }

    private async Task LoadAttendeesAsync(Guid calendarItemId, CalendarItem calendarItem)
    {
        var attendees = await _calendarService.GetAttendeesAsync(calendarItemId);

        // Resolve contacts for all attendees in a single batch DB query.
        var emails = attendees
            .Where(a => !string.IsNullOrEmpty(a.Email))
            .Select(a => a.Email)
            .ToList();

        if (!string.IsNullOrEmpty(calendarItem.OrganizerEmail))
            emails.Add(calendarItem.OrganizerEmail);

        var contacts = await _contactService.GetContactsByAddressesAsync(emails).ConfigureAwait(false);
        var contactLookup = contacts.ToDictionary(c => c.Address, StringComparer.OrdinalIgnoreCase);

        foreach (var attendee in attendees)
        {
            if (!string.IsNullOrEmpty(attendee.Email) && contactLookup.TryGetValue(attendee.Email, out var contact))
                attendee.ResolvedContact = contact;
        }

        // Separate organizer from other attendees to ensure organizer is always first
        var organizer = attendees.FirstOrDefault(a => a.IsOrganizer);
        var nonOrganizerAttendees = attendees.Where(a => !a.IsOrganizer).ToList();

        var attendeesForUi = new List<CalendarEventAttendee>();

        // If the organizer is in the list, add them first
        if (organizer != null)
        {
            attendeesForUi.Add(organizer);
        }
        else if (!string.IsNullOrEmpty(calendarItem.OrganizerEmail))
        {
            // If the organizer is not in the attendees list, create and add them first
            var organizerAttendee = new CalendarEventAttendee
            {
                Id = Guid.NewGuid(),
                CalendarItemId = calendarItem.Id,
                Name = calendarItem.OrganizerDisplayName ?? calendarItem.OrganizerEmail,
                Email = calendarItem.OrganizerEmail,
                IsOrganizer = true,
                AttendenceStatus = AttendeeStatus.Accepted
            };

            if (contactLookup.TryGetValue(calendarItem.OrganizerEmail, out var organizerContact))
                organizerAttendee.ResolvedContact = organizerContact;

            attendeesForUi.Add(organizerAttendee);
        }

        // Add all other attendees after the organizer
        foreach (var item in nonOrganizerAttendees)
        {
            attendeesForUi.Add(item);
        }

        await ExecuteUIThread(() =>
        {
            if (CurrentEvent == null)
                return;

            CurrentEvent.Attendees.Clear();

            foreach (var attendee in attendeesForUi)
            {
                CurrentEvent.Attendees.Add(attendee);
            }

            InitializeEditableAttendees(attendeesForUi);
        });
    }

    private async Task LoadAttachmentsAsync(Guid calendarItemId)
    {
        Attachments.Clear();

        try
        {
            var attachments = await _calendarService.GetAttachmentsAsync(calendarItemId);

            foreach (var attachment in attachments)
            {
                Attachments.Add(new CalendarAttachmentViewModel(attachment));
            }

            OnPropertyChanged(nameof(HasAttachments));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading attachments: {ex.Message}");
        }
    }

    private void InitializeDraft(CalendarItem item)
    {
        Title = item.Title ?? string.Empty;
        Location = item.Location ?? string.Empty;
        IsAllDay = item.IsAllDayEvent;

        var localStart = item.LocalStartDate;
        var localEnd = item.LocalEndDate;

        StartDate = localStart.Date;
        StartTime = localStart.TimeOfDay;
        EndTime = localEnd.TimeOfDay;
        AllDayEndDate = item.IsAllDayEvent ? localEnd.Date : localStart.Date;
        RecurrenceSummary = item.IsRecurringEvent
            ? Translator.CalendarEventDetails_EditSeries
            : string.Empty;

        SelectedShowAsOption = ShowAsOptions.FirstOrDefault(o => o.ShowAs == item.ShowAs) ?? ShowAsOptions.FirstOrDefault();
    }

    private void InitializeEditableAttendees(IEnumerable<CalendarEventAttendee> attendees)
    {
        EditableAttendees.Clear();

        foreach (var attendee in attendees.Where(attendee => !attendee.IsOrganizer && !string.IsNullOrWhiteSpace(attendee.Email)))
        {
            EditableAttendees.Add(new CalendarComposeAttendeeViewModel(attendee.Name, attendee.Email, attendee.ResolvedContact));
        }
    }

    private void InitializeReminderOptions()
    {
        ReminderOptions.Clear();
        ReminderOptions.Add(new ReminderOption(0));

        // Add predefined options from service
        var predefinedMinutes = _calendarService.GetPredefinedReminderMinutes();
        var predefinedOptions = predefinedMinutes.Select(m => new ReminderOption(m)).ToList();

        // Add custom reminders from synced data
        if (Reminders != null)
        {
            foreach (var reminder in Reminders)
            {
                // Convert seconds to minutes
                var minutesDiff = (int)(reminder.DurationInSeconds / 60);

                // Check if this is a custom value not in predefined list
                if (!predefinedMinutes.Contains(minutesDiff))
                {
                    predefinedOptions.Add(new ReminderOption(minutesDiff, isCustom: true));
                }
            }
        }

        // Sort by minutes descending and add to collection
        foreach (var option in predefinedOptions.OrderByDescending(o => o.Minutes))
        {
            ReminderOptions.Add(option);
        }

        var firstReminderMinutes = Reminders?
            .Where(reminder => reminder.DurationInSeconds > 0)
            .OrderBy(reminder => reminder.DurationInSeconds)
            .Select(reminder => (int)(reminder.DurationInSeconds / 60))
            .FirstOrDefault() ?? 0;

        SelectedReminderOption = ReminderOptions.FirstOrDefault(option => option.Minutes == firstReminderMinutes)
                                 ?? ReminderOptions.FirstOrDefault();

        if (SelectedReminderOption != null)
            SelectedReminderOption.IsSelected = true;
    }


    [RelayCommand]
    private async Task SaveAsync()
    {
        if (CurrentEvent == null) return;
        if (CurrentEvent.AssignedCalendar?.IsReadOnly == true)
        {
            _dialogService.ShowReadOnlyCalendarMessage();
            return;
        }

        if (!CanEditEventDetails && !CanEditPersonalOptions)
            return;

        try
        {
            // Capture original state BEFORE making any changes for potential revert
            var originalItem = await _calendarService.GetCalendarItemAsync(CurrentEvent.CalendarItem.Id);
            var originalAttendees = await _calendarService.GetAttendeesAsync(CurrentEvent.CalendarItem.Id);
            var originalReminders = await _calendarService.GetRemindersAsync(CurrentEvent.CalendarItem.Id);
            var updatedItem = CloneCalendarItem(originalItem ?? CurrentEvent.CalendarItem);
            var updatedAttendees = CanEditEventDetails ? BuildEditableAttendees(updatedItem) : originalAttendees;
            var newReminders = BuildReminderDraft(updatedItem.Id);

            if (SelectedShowAsOption != null)
                updatedItem.ShowAs = SelectedShowAsOption.ShowAs;

            if (CanEditEventDetails)
                await ApplyEventDetailsDraftAsync(updatedItem).ConfigureAwait(false);

            await _calendarService.UpdateCalendarItemAsync(updatedItem, updatedAttendees);
            await _calendarService.SaveRemindersAsync(updatedItem.Id, newReminders);

            CurrentEvent = new CalendarItemViewModel(updatedItem);
            await LoadAttendeesAsync(updatedItem.Id, updatedItem);
            Reminders = newReminders;

            if (CanEditEventDetails && ShouldUpdateRecurringChildren(updatedItem))
            {
                await _calendarService.UpdateRecurringChildrenFromSeriesMasterAsync(updatedItem, updatedAttendees, newReminders);
            }

            var operation = CanEditEventDetails
                ? CalendarSynchronizerOperation.UpdateEvent
                : CalendarSynchronizerOperation.UpdateEventPersonalOptions;

            var preparationRequest = new CalendarOperationPreparationRequest(
                operation,
                updatedItem,
                updatedAttendees,
                ResponseMessage: null,
                OriginalItem: originalItem,
                OriginalAttendees: originalAttendees,
                Reminders: newReminders,
                OriginalReminders: originalReminders);

            await _winoRequestDelegator.ExecuteAsync(preparationRequest);

            NavigateBackToCalendar(forceReload: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error saving event: {ex.Message}");
            _dialogService.InfoBarMessage(
                Translator.Info_AttachmentSaveFailedTitle,
                ex.Message,
                InfoBarMessageType.Error);
        }
    }

    private async Task ApplyEventDetailsDraftAsync(CalendarItem item)
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            throw new InvalidOperationException(Translator.CalendarEventCompose_ValidationMissingTitle);
        }

        item.Title = Title.Trim();
        item.Location = Location?.Trim() ?? string.Empty;
        item.Description = GetHtmlNotesAsync == null ? item.Description : await GetHtmlNotesAsync().ConfigureAwait(false);

        if (IsAllDay)
        {
            var start = StartDate.Date;
            var end = AllDayEndDate.Date <= start ? start.AddDays(1) : AllDayEndDate.Date;
            item.StartDate = start;
            item.DurationInSeconds = (end - start).TotalSeconds;
            item.StartTimeZone ??= TimeZoneInfo.Local.Id;
            item.EndTimeZone ??= item.StartTimeZone;
            return;
        }

        var localStart = StartDate.Date + StartTime;
        var localEnd = StartDate.Date + EndTime;
        if (localEnd <= localStart)
            localEnd = localStart.AddMinutes(30);

        item.StartTimeZone ??= TimeZoneInfo.Local.Id;
        item.EndTimeZone ??= item.StartTimeZone;
        item.StartDate = localStart.ToTimeZoneFromLocal(item.StartTimeZone);
        item.DurationInSeconds = (localEnd - localStart).TotalSeconds;
    }

    private List<Reminder> BuildReminderDraft(Guid calendarItemId)
    {
        if (SelectedReminderOption == null || SelectedReminderOption.Minutes <= 0)
            return [];

        return
        [
            new Reminder
            {
                Id = Guid.NewGuid(),
                CalendarItemId = calendarItemId,
                DurationInSeconds = SelectedReminderOption.Minutes * 60L,
                ReminderType = CalendarItemReminderType.Popup
            }
        ];
    }

    private List<CalendarEventAttendee> BuildEditableAttendees(CalendarItem item)
    {
        var attendees = new List<CalendarEventAttendee>();

        if (!string.IsNullOrWhiteSpace(item.OrganizerEmail))
        {
            attendees.Add(new CalendarEventAttendee
            {
                Id = Guid.NewGuid(),
                CalendarItemId = item.Id,
                Name = item.OrganizerDisplayName ?? item.OrganizerEmail,
                Email = item.OrganizerEmail,
                IsOrganizer = true,
                AttendenceStatus = AttendeeStatus.Accepted
            });
        }

        attendees.AddRange(EditableAttendees
            .Where(attendee => !string.IsNullOrWhiteSpace(attendee.Email))
            .Select(attendee => new CalendarEventAttendee
            {
                Id = Guid.NewGuid(),
                CalendarItemId = item.Id,
                Name = attendee.DisplayName,
                Email = attendee.Email,
                IsOrganizer = false,
                AttendenceStatus = AttendeeStatus.NeedsAction
            }));

        return attendees;
    }

    private static CalendarItem CloneCalendarItem(CalendarItem item)
    {
        return new CalendarItem
        {
            Id = item.Id,
            RemoteEventId = item.RemoteEventId,
            Title = item.Title,
            Description = item.Description,
            Location = item.Location,
            StartDate = item.StartDate,
            StartTimeZone = item.StartTimeZone,
            EndTimeZone = item.EndTimeZone,
            DurationInSeconds = item.DurationInSeconds,
            Recurrence = item.Recurrence,
            OrganizerDisplayName = item.OrganizerDisplayName,
            OrganizerEmail = item.OrganizerEmail,
            RecurringCalendarItemId = item.RecurringCalendarItemId,
            IsLocked = item.IsLocked,
            IsHidden = item.IsHidden,
            CustomEventColorHex = item.CustomEventColorHex,
            HtmlLink = item.HtmlLink,
            SnoozedUntil = item.SnoozedUntil,
            Status = item.Status,
            Visibility = item.Visibility,
            ShowAs = item.ShowAs,
            CreatedAt = item.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            CalendarId = item.CalendarId,
            AssignedCalendar = item.AssignedCalendar
        };
    }

    private static bool ShouldUpdateRecurringChildren(CalendarItem item)
    {
        var account = item.AssignedCalendar?.MailAccount;
        return item.IsRecurringParent &&
               account?.ProviderType == MailProviderType.IMAP4 &&
               account.ServerInformation?.CalendarSupportMode is ImapCalendarSupportMode.LocalOnly or ImapCalendarSupportMode.CalDav;
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (CurrentEvent == null) return;
        if (!CanDeleteEvent)
        {
            if (CurrentEvent.AssignedCalendar?.IsReadOnly == true)
                _dialogService.ShowReadOnlyCalendarMessage();
            return;
        }

        // If the event is a master recurring event, ask for confirmation
        if (CurrentEvent.IsRecurringParent)
        {
            var confirmed = await _dialogService.ShowConfirmationDialogAsync(
                Translator.DialogMessage_DeleteRecurringSeriesMessage,
                Translator.DialogMessage_DeleteRecurringSeriesTitle,
                Translator.Buttons_Delete);

            if (!confirmed) return;
        }

        try
        {
            var preparationRequest = new CalendarOperationPreparationRequest(
                CalendarSynchronizerOperation.DeleteEvent,
                CurrentEvent.CalendarItem,
                null);

            await _winoRequestDelegator.ExecuteAsync(preparationRequest);

            NavigateBackToCalendar(forceReload: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error deleting calendar event: {ex.Message}");
        }
    }

    private void NavigateBackToCalendar(bool forceReload)
    {
        var navigationDate = CurrentEvent?.CalendarItem.LocalStartDate ?? DateTime.Now;

        _navigationService.Navigate(
            WinoPage.CalendarPage,
            new CalendarPageNavigationArgs
            {
                NavigationDate = navigationDate,
                ForceReload = forceReload
            });
    }

    public override async Task KeyboardShortcutHook(KeyboardShortcutTriggerDetails args)
    {
        if (args.Handled || args.Mode != WinoApplicationMode.Calendar || args.Action != KeyboardShortcutAction.Delete)
            return;

        await DeleteAsync();
        args.Handled = true;
    }

    public async Task<List<AccountContact>> SearchContactsAsync(string queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText) || queryText.Length < 2)
            return [];

        return await _contactService.GetAddressInformationAsync(queryText).ConfigureAwait(false);
    }

    public async Task<CalendarComposeAttendeeViewModel> GetAttendeeAsync(string tokenText)
    {
        if (!EmailValidator.Validate(tokenText))
            return null;

        var existing = EditableAttendees.Any(attendee => attendee.Email.Equals(tokenText, StringComparison.OrdinalIgnoreCase));
        if (existing)
            return null;

        var info = await _contactService.GetAddressInformationByAddressAsync(tokenText).ConfigureAwait(false);
        if (info != null)
            return CalendarComposeAttendeeViewModel.FromContact(info);

        return new CalendarComposeAttendeeViewModel(string.Empty, tokenText);
    }

    public void AddAttendee(CalendarComposeAttendeeViewModel attendee)
    {
        if (attendee == null || EditableAttendees.Any(existing => existing.Email.Equals(attendee.Email, StringComparison.OrdinalIgnoreCase)))
            return;

        EditableAttendees.Add(attendee);
    }

    [RelayCommand]
    private void RemoveAttendee(CalendarComposeAttendeeViewModel attendee)
    {
        if (attendee == null)
            return;

        EditableAttendees.Remove(attendee);
    }

    public void NotifyAddressExists()
    {
        _dialogService.InfoBarMessage(
            Translator.Info_ContactExistsTitle,
            Translator.Info_ContactExistsMessage,
            InfoBarMessageType.Warning);
    }

    public void NotifyInvalidEmail(string address)
    {
        _dialogService.InfoBarMessage(
            Translator.Info_InvalidAddressTitle,
            string.Format(Translator.Info_InvalidAddressMessage, address),
            InfoBarMessageType.Warning);
    }

    [RelayCommand]
    private Task JoinOnlineAsync()
    {
        if (CurrentEvent == null || string.IsNullOrEmpty(CurrentEvent.CalendarItem.HtmlLink))
            return Task.CompletedTask;

        return _nativeAppService.LaunchUriAsync(new Uri(CurrentEvent.CalendarItem.HtmlLink));
    }

    [RelayCommand]
    private Task CreateTestNotificationAsync()
    {
        if (CurrentEvent?.CalendarItem == null)
            return Task.CompletedTask;

        var reminderDurationInSeconds = Reminders?
            .Where(x => x.DurationInSeconds > 0)
            .OrderByDescending(x => x.DurationInSeconds)
            .Select(x => x.DurationInSeconds)
            .FirstOrDefault() ?? 0;

        if (reminderDurationInSeconds <= 0)
            reminderDurationInSeconds = Math.Max(_preferencesService.DefaultReminderDurationInSeconds, 30 * 60);

        return _notificationBuilder.CreateCalendarReminderNotificationAsync(CurrentEvent.CalendarItem, reminderDurationInSeconds);
    }

    [RelayCommand]
    private void ToggleRsvpPanel()
    {
        IsRsvpPanelVisible = !IsRsvpPanelVisible;

        if (IsRsvpPanelVisible && CurrentEvent?.CalendarItem != null)
        {
            // Initialize selection based on current status
            foreach (var item in RsvpStatusOptions)
            {
                item.IsSelected = CurrentEvent?.CalendarItem?.Status == item.Status;
            }
        }
    }

    [RelayCommand]
    private void CloseRsvpPanel()
    {
        IsRsvpPanelVisible = false;
        RsvpMessage = string.Empty;
    }

    [RelayCommand]
    private async Task SendRsvpResponse(AttendeeStatus status)
    {
        if (CurrentEvent == null) return;
        if (!CanRespond)
        {
            if (CurrentEvent.AssignedCalendar?.IsReadOnly == true)
                _dialogService.ShowReadOnlyCalendarMessage();
            return;
        }

        try
        {
            // Get the optional response message if user wants to include it
            var responseMessage = IncludeRsvpMessage ? RsvpMessage : null;

            // Map status to operation
            CalendarSynchronizerOperation operation = status switch
            {
                AttendeeStatus.Accepted => CalendarSynchronizerOperation.AcceptEvent,
                AttendeeStatus.Tentative => CalendarSynchronizerOperation.TentativeEvent,
                AttendeeStatus.Declined => CalendarSynchronizerOperation.DeclineEvent,
                _ => throw new InvalidOperationException($"Invalid RSVP status: {status}")
            };

            // Create preparation request with the optional message
            var preparationRequest = new CalendarOperationPreparationRequest(
                operation,
                CurrentEvent.CalendarItem,
                null,
                responseMessage);

            await _winoRequestDelegator.ExecuteAsync(preparationRequest);

            // Reload attendees to get the updated status from the server
            await LoadAttendeesAsync(CurrentEvent.CalendarItem.Id, CurrentEvent.CalendarItem);

            OnPropertyChanged(nameof(CurrentRsvpText));
            OnPropertyChanged(nameof(CurrentRsvpStatus));

            CloseRsvpPanel();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error sending RSVP response: {ex.Message}");
            _dialogService.InfoBarMessage(
                Translator.Info_AttachmentSaveFailedTitle,
                ex.Message,
                InfoBarMessageType.Error);
        }
    }

    [RelayCommand]
    private async Task ViewSeriesAsync()
    {
        if (CurrentEvent == null || !CurrentEvent.IsRecurringChild) return;

        try
        {
            // Get the master event from the recurring series
            var masterEventId = CurrentEvent.CalendarItem.RecurringCalendarItemId.Value;
            var masterEvent = await _calendarService.GetCalendarItemAsync(masterEventId);

            if (masterEvent == null) return;

            // Load the master event without navigation
            var target = new CalendarItemTarget(masterEvent, CalendarEventTargetType.Series);
            await LoadCalendarItemTargetAsync(target);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading series: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task OpenAttachmentAsync(CalendarAttachmentViewModel attachmentViewModel)
    {
        if (attachmentViewModel == null || CurrentEvent?.CalendarItem == null) return;

        try
        {
            attachmentViewModel.IsBusy = true;

            // If not downloaded, download it first
            if (!attachmentViewModel.IsDownloaded)
            {
                await DownloadAttachmentAsync(attachmentViewModel);
            }

            // Launch the file
            if (!string.IsNullOrEmpty(attachmentViewModel.Attachment.LocalFilePath) &&
                File.Exists(attachmentViewModel.Attachment.LocalFilePath))
            {
                await _nativeAppService.LaunchFileAsync(attachmentViewModel.Attachment.LocalFilePath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open calendar attachment.");
            _dialogService.InfoBarMessage(
                Translator.Info_AttachmentOpenFailedTitle,
                Translator.Info_AttachmentOpenFailedMessage,
                InfoBarMessageType.Error);
        }
        finally
        {
            attachmentViewModel.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAttachmentAsync(CalendarAttachmentViewModel attachmentViewModel)
    {
        if (attachmentViewModel == null) return;

        try
        {
            attachmentViewModel.IsBusy = true;

            var pickedPath = await _dialogService.PickWindowsFolderAsync();
            if (string.IsNullOrEmpty(pickedPath)) return;

            // Download if not already downloaded
            if (!attachmentViewModel.IsDownloaded)
            {
                await DownloadAttachmentAsync(attachmentViewModel);
            }

            // Copy to selected location
            if (!string.IsNullOrEmpty(attachmentViewModel.Attachment.LocalFilePath) &&
                File.Exists(attachmentViewModel.Attachment.LocalFilePath))
            {
                var destinationPath = Path.Combine(pickedPath, attachmentViewModel.FileName);
                File.Copy(attachmentViewModel.Attachment.LocalFilePath, destinationPath, overwrite: true);

                _dialogService.InfoBarMessage(
                    Translator.Info_AttachmentSaveSuccessTitle,
                    Translator.Info_AttachmentSaveSuccessMessage,
                    InfoBarMessageType.Success);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save calendar attachment.");
            _dialogService.InfoBarMessage(
                Translator.Info_AttachmentSaveFailedTitle,
                Translator.Info_AttachmentSaveFailedMessage,
                InfoBarMessageType.Error);
        }
        finally
        {
            attachmentViewModel.IsBusy = false;
        }
    }

    private async Task DownloadAttachmentAsync(CalendarAttachmentViewModel attachmentViewModel)
    {
        if (CurrentEvent?.CalendarItem == null) return;

        // Create attachments folder for this calendar item
        var attachmentsFolder = Path.Combine(
            _nativeAppService.GetCalendarAttachmentsFolderPath(),
            CurrentEvent.CalendarItem.Id.ToString());

        Directory.CreateDirectory(attachmentsFolder);

        var localFilePath = Path.Combine(attachmentsFolder, attachmentViewModel.FileName);

        // Download attachment using synchronizer
        await SynchronizationManager.Instance.DownloadCalendarAttachmentAsync(
            CurrentEvent.CalendarItem,
            attachmentViewModel.Attachment,
            localFilePath);

        // Mark as downloaded
        await _calendarService.MarkAttachmentDownloadedAsync(
            attachmentViewModel.Id,
            localFilePath);

        // Update view model
        attachmentViewModel.Attachment.IsDownloaded = true;
        attachmentViewModel.Attachment.LocalFilePath = localFilePath;
        OnPropertyChanged(nameof(attachmentViewModel.IsDownloaded));
    }
}

public partial class ReminderOption : ObservableObject
{
    public int Minutes { get; }
    public bool IsCustom { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string DisplayText
    {
        get
        {
            if (Minutes <= 0)
                return Translator.CalendarReminder_None;

            if (Minutes >= 60)
            {
                var hours = Minutes / 60;
                return string.Format(hours == 1 ? Translator.CalendarReminder_HourOption : Translator.CalendarReminder_HoursOption, hours);
            }

            return string.Format(Minutes == 1 ? Translator.CalendarReminder_MinuteOption : Translator.CalendarReminder_MinutesOption, Minutes);
        }
    }

    public ReminderOption(int minutes, bool isCustom = false)
    {
        Minutes = minutes;
        IsCustom = isCustom;
    }
}

public partial class ShowAsOption : ObservableObject
{
    public CalendarItemShowAs ShowAs { get; }

    public string DisplayText
    {
        get
        {
            return ShowAs switch
            {
                CalendarItemShowAs.Free => Translator.CalendarShowAs_Free,
                CalendarItemShowAs.Tentative => Translator.CalendarShowAs_Tentative,
                CalendarItemShowAs.Busy => Translator.CalendarShowAs_Busy,
                CalendarItemShowAs.OutOfOffice => Translator.CalendarShowAs_OutOfOffice,
                CalendarItemShowAs.WorkingElsewhere => Translator.CalendarShowAs_WorkingElsewhere,
                _ => Translator.CalendarShowAs_Busy
            };
        }
    }

    public ShowAsOption(CalendarItemShowAs showAs)
    {
        ShowAs = showAs;
    }
}

public partial class RsvpStatusOption : ObservableObject
{
    public CalendarItemStatus Status { get; }

    public string StatusText
    {
        get
        {
            return Status switch
            {
                CalendarItemStatus.Accepted => Translator.CalendarEventResponse_Accept,
                CalendarItemStatus.Tentative => Translator.CalendarEventResponse_Tentative,
                CalendarItemStatus.Cancelled => Translator.CalendarEventResponse_Decline,
                _ => Translator.CalendarEventResponse_Accept
            };
        }
    }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public RsvpStatusOption(CalendarItemStatus status)
    {
        Status = status;
    }
}
