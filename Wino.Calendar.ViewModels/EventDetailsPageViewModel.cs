using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
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
    private CalendarItemViewModel _currentEvent;

    [ObservableProperty]
    private CalendarItemViewModel _seriesParent;

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
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
    }

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        base.OnNavigatedFrom(mode, parameters);

        Messenger.Send(new DetailsPageStateChangedMessage(false));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        // TODO: Implement saving
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
}
