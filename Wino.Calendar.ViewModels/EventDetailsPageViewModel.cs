using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
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

    public CalendarSettings CurrentSettings { get; }

    #region Details

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanViewSeries))]
    [NotifyPropertyChangedFor(nameof(CanEditSeries))]
    [NotifyPropertyChangedFor(nameof(IsCurrentUserOrganizer))]
    public partial CalendarItemViewModel CurrentEvent { get; set; }
    [ObservableProperty]
    public partial CalendarItemViewModel SeriesParent { get; set; }
    [ObservableProperty]
    public partial List<Reminder> Reminders { get; set; }

    public ObservableCollection<ReminderOption> ReminderOptions { get; } = new ObservableCollection<ReminderOption>();

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
    public bool IsCurrentUserOrganizer => CurrentEvent?.Attendees?.Any(a => a.IsOrganizer) ?? true;

    #endregion

    #region Show As Options

    public List<CalendarItemShowAs> ShowAsOptions { get; } =
    [
        CalendarItemShowAs.Free,
        CalendarItemShowAs.Tentative,
        CalendarItemShowAs.Busy,
        CalendarItemShowAs.OutOfOffice,
        CalendarItemShowAs.WorkingElsewhere
    ];

    [ObservableProperty]
    public partial CalendarItemShowAs SelectedShowAs { get; set; } = CalendarItemShowAs.Busy;

    #endregion

    public EventDetailsPageViewModel(ICalendarService calendarService,
                                     INativeAppService nativeAppService,
                                     IPreferencesService preferencesService,
                                     IMailDialogService dialogService,
                                     IWinoRequestDelegator winoRequestDelegator,
                                     INavigationService navigationService)
    {
        _calendarService = calendarService;
        _nativeAppService = nativeAppService;
        _preferencesService = preferencesService;
        _dialogService = dialogService;
        _winoRequestDelegator = winoRequestDelegator;
        _navigationService = navigationService;

        CurrentSettings = _preferencesService.GetCurrentCalendarSettings();
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        Messenger.Send(new DetailsPageStateChangedMessage(true));

        if (parameters == null || parameters is not CalendarItemTarget args)
            return;

        await LoadCalendarItemTargetAsync(args);
    }

    private async Task LoadCalendarItemTargetAsync(CalendarItemTarget target)
    {
        try
        {
            var currentEventItem = await _calendarService.GetCalendarItemTargetAsync(target);

            if (currentEventItem == null)
                return;

            CurrentEvent = new CalendarItemViewModel(currentEventItem);

            var attendees = await _calendarService.GetAttendeesAsync(currentEventItem.EventTrackingId);

            foreach (var item in attendees)
            {
                CurrentEvent.Attendees.Add(item);
            }

            // Load reminders for this calendar item
            Reminders = await _calendarService.GetRemindersAsync(currentEventItem.EventTrackingId);
            InitializeReminderOptions();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
    }

    private void InitializeReminderOptions()
    {
        ReminderOptions.Clear();

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

        // Set selected state based on current reminders
        if (Reminders != null)
        {
            foreach (var reminder in Reminders)
            {
                // Convert seconds to minutes
                var minutesDiff = (int)(reminder.DurationInSeconds / 60);

                var matchingOption = ReminderOptions.FirstOrDefault(o => o.Minutes == minutesDiff);
                matchingOption?.IsSelected = true;
            }
        }
    }


    [RelayCommand]
    private async Task SaveAsync()
    {
        if (CurrentEvent == null) return;

        try
        {
            // Get selected reminder options
            var selectedOptions = ReminderOptions.Where(o => o.IsSelected).ToList();

            // Create separate Reminder entities for each selected option
            var newReminders = new List<Reminder>();

            foreach (var option in selectedOptions)
            {
                var durationInSeconds = option.Minutes * 60; // Convert minutes to seconds

                newReminders.Add(new Reminder
                {
                    Id = Guid.NewGuid(),
                    CalendarItemId = CurrentEvent.Id,
                    DurationInSeconds = durationInSeconds,
                    ReminderType = CalendarItemReminderType.Popup
                });
            }

            // Save reminders to database
            await _calendarService.SaveRemindersAsync(CurrentEvent.CalendarItem.EventTrackingId, newReminders);
            Reminders = newReminders;

            _navigationService.GoBack();
            // TODO: Implement saving other event details
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error saving event: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (CurrentEvent == null) return;

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

            // Navigate back after successful deletion
            _navigationService.GoBack();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error deleting calendar event: {ex.Message}");
        }
    }

    [RelayCommand]
    private Task JoinOnlineAsync()
    {
        if (CurrentEvent == null || string.IsNullOrEmpty(CurrentEvent.CalendarItem.HtmlLink))
            return Task.CompletedTask;

        return _nativeAppService.LaunchUriAsync(new Uri(CurrentEvent.CalendarItem.HtmlLink));
    }

    [RelayCommand]
    private async Task RespondAsync(CalendarItemStatus status)
    {
        if (CurrentEvent == null) return;

        // TODO: Implement response
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
            if (Minutes >= 60)
            {
                var hours = Minutes / 60;
                return hours == 1 ? "1 Hour" : $"{hours} Hours";
            }
            return Minutes == 1 ? "1 Minute" : $"{Minutes} Minutes";
        }
    }

    public ReminderOption(int minutes, bool isCustom = false)
    {
        Minutes = minutes;
        IsCustom = isCustom;
    }
}
