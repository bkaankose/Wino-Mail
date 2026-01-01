using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MoreLinq;
using Serilog;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Calendar.ViewModels.Messages;
using Wino.Core.Domain.Collections;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Calendar.CalendarTypeStrategies;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.ViewModels;

public partial class CalendarPageViewModel : CalendarBaseViewModel,
    IRecipient<LoadCalendarMessage>,
    IRecipient<CalendarItemDeleted>,
    IRecipient<CalendarSettingsUpdatedMessage>,
    IRecipient<CalendarItemTappedMessage>,
    IRecipient<CalendarItemDoubleTappedMessage>,
    IRecipient<CalendarItemRightTappedMessage>
{
    #region Quick Event Creation

    [ObservableProperty]
    private bool _isQuickEventDialogOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedQuickEventAccountCalendarName))]
    [NotifyCanExecuteChangedFor(nameof(SaveQuickEventCommand))]
    private AccountCalendarViewModel _selectedQuickEventAccountCalendar;

    public string SelectedQuickEventAccountCalendarName
    {
        get
        {
            return SelectedQuickEventAccountCalendar == null ? "Pick a calendar" : SelectedQuickEventAccountCalendar.Name;
        }
    }

    [ObservableProperty]
    private List<string> _hourSelectionStrings;

    // To be able to revert the values when the user enters an invalid time.
    private string _previousSelectedStartTimeString;
    private string _previousSelectedEndTimeString;

    [ObservableProperty]
    private DateTime? _selectedQuickEventDate;

    [ObservableProperty]
    private bool _isAllDay;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveQuickEventCommand))]
    private string _selectedStartTimeString;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveQuickEventCommand))]
    private string _selectedEndTimeString;

    [ObservableProperty]
    private string _location;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveQuickEventCommand))]
    private string _eventName;

    public DateTime QuickEventStartTime => SelectedQuickEventDate.Value.Date.Add(CurrentSettings.GetTimeSpan(SelectedStartTimeString).Value);
    public DateTime QuickEventEndTime => SelectedQuickEventDate.Value.Date.Add(CurrentSettings.GetTimeSpan(SelectedEndTimeString).Value);

    public bool CanSaveQuickEvent => SelectedQuickEventAccountCalendar != null &&
                                    !string.IsNullOrWhiteSpace(EventName) &&
                                    !string.IsNullOrWhiteSpace(SelectedStartTimeString) &&
                                    !string.IsNullOrWhiteSpace(SelectedEndTimeString) &&
                                    QuickEventEndTime > QuickEventStartTime;

    #endregion

    #region Data Initialization

    [ObservableProperty]
    private CalendarOrientation _calendarOrientation = CalendarOrientation.Horizontal;

    [ObservableProperty]
    private DayRangeCollection _dayRanges = [];

    [ObservableProperty]
    private int _selectedDateRangeIndex;

    [ObservableProperty]
    private DayRangeRenderModel _selectedDayRange;

    [ObservableProperty]
    private bool _isCalendarEnabled = true;

    #endregion

    #region Event Details

    public event EventHandler DetailsShowCalendarItemChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEventDetailsVisible))]
    private CalendarItemViewModel _displayDetailsCalendarItemViewModel;

    public bool IsEventDetailsVisible => DisplayDetailsCalendarItemViewModel != null;

    #endregion

    // TODO: Get rid of some of the items if we have too many.
    private const int maxDayRangeSize = 10;

    private readonly ICalendarService _calendarService;
    private readonly INavigationService _navigationService;
    private readonly IKeyPressService _keyPressService;
    private readonly IPreferencesService _preferencesService;
    private readonly IWinoRequestDelegator _winoRequestDelegator;

    // Store latest rendered options.
    private CalendarDisplayType _currentDisplayType;
    private int _displayDayCount;

    private SemaphoreSlim _calendarLoadingSemaphore = new(1);
    private bool isLoadMoreBlocked = false;

    [ObservableProperty]
    private CalendarSettings _currentSettings;

    public IStatePersistanceService StatePersistanceService { get; }
    public IAccountCalendarStateService AccountCalendarStateService { get; }

    public CalendarPageViewModel(IStatePersistanceService statePersistanceService,
                                 ICalendarService calendarService,
                                 INavigationService navigationService,
                                 IKeyPressService keyPressService,
                                 IAccountCalendarStateService accountCalendarStateService,
                                 IPreferencesService preferencesService,
                                 IWinoRequestDelegator winoRequestDelegator)
    {
        StatePersistanceService = statePersistanceService;
        AccountCalendarStateService = accountCalendarStateService;

        _calendarService = calendarService;
        _navigationService = navigationService;
        _keyPressService = keyPressService;
        _preferencesService = preferencesService;
        _winoRequestDelegator = winoRequestDelegator;

        AccountCalendarStateService.AccountCalendarSelectionStateChanged += UpdateAccountCalendarRequested;
        AccountCalendarStateService.CollectiveAccountGroupSelectionStateChanged += AccountCalendarStateCollectivelyChanged;
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        Messenger.Register<LoadCalendarMessage>(this);
        Messenger.Register<CalendarSettingsUpdatedMessage>(this);
        Messenger.Register<CalendarItemTappedMessage>(this);
        Messenger.Register<CalendarItemDoubleTappedMessage>(this);
        Messenger.Register<CalendarItemRightTappedMessage>(this);
    }
    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<LoadCalendarMessage>(this);
        Messenger.Unregister<CalendarSettingsUpdatedMessage>(this);
        Messenger.Unregister<CalendarItemTappedMessage>(this);
        Messenger.Unregister<CalendarItemDoubleTappedMessage>(this);
        Messenger.Unregister<CalendarItemRightTappedMessage>(this);
    }

    private void AccountCalendarStateCollectivelyChanged(object sender, GroupedAccountCalendarViewModel e)
        => FilterActiveCalendars(DayRanges);

    private void UpdateAccountCalendarRequested(object sender, AccountCalendarViewModel e)
        => FilterActiveCalendars(DayRanges);

    private async void FilterActiveCalendars(IEnumerable<DayRangeRenderModel> dayRangeRenderModels)
    {
        await ExecuteUIThread(() =>
        {
            var days = dayRangeRenderModels.SelectMany(a => a.CalendarDays);

            days.ForEach(a => a.EventsCollection.FilterByCalendars(AccountCalendarStateService.ActiveCalendars.Select(a => a.Id)));

            DisplayDetailsCalendarItemViewModel = null;
        });
    }

    // TODO: Replace when calendar settings are updated.
    // Should be a field ideally.
    private BaseCalendarTypeDrawingStrategy GetDrawingStrategy(CalendarDisplayType displayType)
    {
        return displayType switch
        {
            CalendarDisplayType.Day => new DayCalendarDrawingStrategy(CurrentSettings),
            CalendarDisplayType.Week => new WeekCalendarDrawingStrategy(CurrentSettings),
            CalendarDisplayType.Month => new MonthCalendarDrawingStrategy(CurrentSettings),
            _ => throw new NotImplementedException(),
        };
    }

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        // Do not call base method because that will unregister messenger recipient.
        // This is a singleton view model and should not be unregistered.
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (mode == NavigationMode.Back) return;

        RefreshSettings();

        // Automatically select the first primary calendar for quick event dialog.
        SelectedQuickEventAccountCalendar = AccountCalendarStateService.ActiveCalendars.FirstOrDefault(a => a.IsPrimary);
    }

    [RelayCommand]
    private void NavigateSeries()
    {
        if (DisplayDetailsCalendarItemViewModel == null) return;

        NavigateEvent(DisplayDetailsCalendarItemViewModel, CalendarEventTargetType.Series);
    }

    [RelayCommand]
    private void NavigateEventDetails()
    {
        if (DisplayDetailsCalendarItemViewModel == null) return;

        NavigateEvent(DisplayDetailsCalendarItemViewModel, CalendarEventTargetType.Single);
    }

    private void NavigateEvent(CalendarItemViewModel calendarItemViewModel, CalendarEventTargetType calendarEventTargetType)
    {
        var target = new CalendarItemTarget(calendarItemViewModel.CalendarItem, calendarEventTargetType);
        _navigationService.Navigate(WinoPage.EventDetailsPage, target);
    }

    [RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanSaveQuickEvent))]
    private async Task SaveQuickEventAsync()
    {
        try
        {
            var startDate = IsAllDay ? SelectedQuickEventDate.Value.Date : QuickEventStartTime;
            var endDate = IsAllDay ? SelectedQuickEventDate.Value.Date.AddDays(1) : QuickEventEndTime;
            var durationSeconds = (endDate - startDate).TotalSeconds;

            // Get the user's current timezone from the system
            var currentTimeZone = TimeZoneInfo.Local.Id;

            var calendarItem = new CalendarItem
            {
                Id = Guid.NewGuid(),
                CalendarId = SelectedQuickEventAccountCalendar.Id,
                StartDate = startDate,
                DurationInSeconds = durationSeconds,
                StartTimeZone = currentTimeZone,
                EndTimeZone = currentTimeZone,
                CreatedAt = DateTime.UtcNow,
                Description = string.Empty,
                Location = Location ?? string.Empty,
                Title = EventName,
                IsHidden = false,
                AssignedCalendar = SelectedQuickEventAccountCalendar
            };

            // Close dialog first
            IsQuickEventDialogOpen = false;

            // Save to local database first
            // await _calendarService.CreateNewCalendarItemAsync(calendarItem, null);

            // Queue the request via delegator
            var preparationRequest = new CalendarOperationPreparationRequest(CalendarSynchronizerOperation.CreateEvent, calendarItem, null);
            await _winoRequestDelegator.ExecuteAsync(preparationRequest);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error creating quick event");
            // Re-open dialog if there was an error
            IsQuickEventDialogOpen = true;
        }
    }

    [RelayCommand]
    private void MoreDetails()
    {
        // TODO: Navigate to advanced event creation  page with existing parameters.
    }

    public void SelectQuickEventTimeRange(TimeSpan startTime, TimeSpan endTime)
    {
        IsAllDay = false;

        SelectedStartTimeString = CurrentSettings.GetTimeString(startTime);
        SelectedEndTimeString = CurrentSettings.GetTimeString(endTime);
    }

    // Manage event detail popup context and select-unselect the proper items.
    // Item selection rules are defined in the selection method.
    partial void OnDisplayDetailsCalendarItemViewModelChanging(CalendarItemViewModel oldValue, CalendarItemViewModel newValue)
    {
        if (oldValue != null)
        {
            UnselectCalendarItem(oldValue);
        }

        if (newValue != null)
        {
            SelectCalendarItem(newValue);
        }
    }

    // Notify view that the detail context changed.
    // This will align the event detail popup to the selected event.
    partial void OnDisplayDetailsCalendarItemViewModelChanged(CalendarItemViewModel value)
        => DetailsShowCalendarItemChanged?.Invoke(this, EventArgs.Empty);

    private void RefreshSettings()
    {
        CurrentSettings = _preferencesService.GetCurrentCalendarSettings();

        // Populate the hour selection strings.
        var timeStrings = new List<string>();

        for (int hour = 0; hour < 24; hour++)
        {
            for (int minute = 0; minute < 60; minute += 30)
            {
                var time = new DateTime(1, 1, 1, hour, minute, 0);

                if (CurrentSettings.DayHeaderDisplayType == DayHeaderDisplayType.TwentyFourHour)
                {
                    timeStrings.Add(time.ToString("HH:mm"));
                }
                else
                {
                    timeStrings.Add(time.ToString("h:mm tt"));
                }
            }
        }

        HourSelectionStrings = timeStrings;
    }

    partial void OnIsCalendarEnabledChanging(bool oldValue, bool newValue) => Messenger.Send(new CalendarEnableStatusChangedMessage(newValue));

    private bool ShouldResetDayRanges(LoadCalendarMessage message)
    {
        if (message.ForceRedraw) return true;

        // Never reset if the initiative is from the app.
        if (message.CalendarInitInitiative == CalendarInitInitiative.App) return false;

        // 1. Display type is different.
        // 2. Day display count is different.
        // 3. Display date is not in the visible range.

        if (DayRanges.DisplayRange == null) return false;

        return
            (_currentDisplayType != StatePersistanceService.CalendarDisplayType ||
            _displayDayCount != StatePersistanceService.DayDisplayCount ||
            !(message.DisplayDate >= DayRanges.DisplayRange.StartDate && message.DisplayDate <= DayRanges.DisplayRange.EndDate));
    }

    private void AdjustCalendarOrientation()
    {
        // Orientation only changes when we should reset.
        // Handle the FlipView orientation here.
        // We don't want to change the orientation while the item manipulation is going on.
        // That causes a glitch in the UI.

        bool isRequestedVerticalCalendar = StatePersistanceService.CalendarDisplayType == CalendarDisplayType.Month;
        bool isLastRenderedVerticalCalendar = _currentDisplayType == CalendarDisplayType.Month;

        if (isRequestedVerticalCalendar && !isLastRenderedVerticalCalendar)
        {
            CalendarOrientation = CalendarOrientation.Vertical;
        }
        else
        {
            CalendarOrientation = CalendarOrientation.Horizontal;
        }
    }

    public async void Receive(LoadCalendarMessage message)
    {
        await _calendarLoadingSemaphore.WaitAsync();

        try
        {
            await ExecuteUIThread(() => IsCalendarEnabled = false);

            if (ShouldResetDayRanges(message))
            {
                Debug.WriteLine("Will reset day ranges.");
                await ClearDayRangeModelsAsync();
            }
            else if (ShouldScrollToItem(message))
            {
                // Scroll to the selected date.
                Messenger.Send(new ScrollToDateMessage(message.DisplayDate));
                Debug.WriteLine("Scrolling to selected date.");
                return;
            }

            AdjustCalendarOrientation();

            // This will replace the whole collection because the user initiated a new render.
            await RenderDatesAsync(message.CalendarInitInitiative,
                                   message.DisplayDate,
                                   CalendarLoadDirection.Replace);

            // Scroll to the current hour.
            Messenger.Send(new ScrollToHourMessage(TimeSpan.FromHours(DateTime.Now.Hour)));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while loading calendar.");
            Debugger.Break();
        }
        finally
        {
            _calendarLoadingSemaphore.Release();

            await ExecuteUIThread(() => IsCalendarEnabled = true);
        }
    }


    private async Task AddDayRangeModelAsync(DayRangeRenderModel dayRangeRenderModel)
    {
        if (dayRangeRenderModel == null) return;

        await ExecuteUIThread(() =>
        {
            DayRanges.Add(dayRangeRenderModel);
        });
    }

    private async Task InsertDayRangeModelAsync(DayRangeRenderModel dayRangeRenderModel, int index)
    {
        if (dayRangeRenderModel == null) return;

        await ExecuteUIThread(() =>
        {
            DayRanges.Insert(index, dayRangeRenderModel);
        });
    }

    private async Task RemoveDayRangeModelAsync(DayRangeRenderModel dayRangeRenderModel)
    {
        if (dayRangeRenderModel == null) return;

        await ExecuteUIThread(() =>
        {
            DayRanges.Remove(dayRangeRenderModel);
        });
    }

    private async Task ClearDayRangeModelsAsync()
    {
        await ExecuteUIThread(() =>
        {
            DayRanges.Clear();
        });
    }

    private async Task RenderDatesAsync(CalendarInitInitiative calendarInitInitiative,
                                        DateTime? loadingDisplayDate = null,
                                        CalendarLoadDirection calendarLoadDirection = CalendarLoadDirection.Replace)
    {
        isLoadMoreBlocked = calendarLoadDirection == CalendarLoadDirection.Replace;

        // This is the part we arrange the flip view calendar logic.

        /* Loading for a month of the selected date is fine.
         * If the selected date is in the loaded range, we'll just change the selected flip index to scroll.
         * If the selected date is not in the loaded range:
         * 1. Detect the direction of the scroll.
         * 2. Load the next month.
         * 3. Replace existing month with the new month.
         */

        // 2 things are important: How many items should 1 flip have, and, where we should start loading?

        // User initiated renders must always have a date to start with.
        if (calendarInitInitiative == CalendarInitInitiative.User) Guard.IsNotNull(loadingDisplayDate, nameof(loadingDisplayDate));

        var strategy = GetDrawingStrategy(StatePersistanceService.CalendarDisplayType);
        var displayDate = loadingDisplayDate.GetValueOrDefault();

        // How many days should be placed in 1 flip view item?
        int eachFlipItemCount = strategy.GetRenderDayCount(displayDate, StatePersistanceService.DayDisplayCount);

        DateRange flipLoadRange = null;


        if (calendarInitInitiative == CalendarInitInitiative.User || DayRanges.DisplayRange == null)
        {
            flipLoadRange = strategy.GetRenderDateRange(displayDate, StatePersistanceService.DayDisplayCount);
        }
        else
        {
            // App is trying to load.
            // This should be based on direction. We'll load the next or previous range.
            // DisplayDate is either the start or end date of the current visible range.

            if (calendarLoadDirection == CalendarLoadDirection.Previous)
            {
                flipLoadRange = strategy.GetPreviousDateRange(DayRanges.DisplayRange, StatePersistanceService.DayDisplayCount);
            }
            else
            {
                flipLoadRange = strategy.GetNextDateRange(DayRanges.DisplayRange, StatePersistanceService.DayDisplayCount);
            }
        }

        // Create day ranges for each flip item until we reach the total days to load.
        int totalFlipItemCount = (int)Math.Ceiling((double)flipLoadRange.TotalDays / eachFlipItemCount);

        List<DayRangeRenderModel> renderModels = new();

        for (int i = 0; i < totalFlipItemCount; i++)
        {
            var startDate = flipLoadRange.StartDate.AddDays(i * eachFlipItemCount);
            var endDate = startDate.AddDays(eachFlipItemCount);

            var range = new DateRange(startDate, endDate);
            var renderOptions = new CalendarRenderOptions(range, CurrentSettings);

            var dayRangeHeaderModel = new DayRangeRenderModel(renderOptions);
            renderModels.Add(dayRangeHeaderModel);
        }

        // Dates are loaded. Now load the events for them.
        foreach (var renderModel in renderModels)
        {
            await InitializeCalendarEventsForDayRangeAsync(renderModel).ConfigureAwait(false);
        }

        // Filter by active calendars. This is a quick operation, and things are not on the UI yet.
        FilterActiveCalendars(renderModels);

        CalendarLoadDirection animationDirection = calendarLoadDirection;

        //bool removeCurrent = calendarLoadDirection == CalendarLoadDirection.Replace;

        if (calendarLoadDirection == CalendarLoadDirection.Replace)
        {
            // New date ranges are being replaced.
            // We must preserve existing selection if any, add the items before/after the current one, remove the current one.
            // This will make sure the new dates are animated in the correct direction.

            isLoadMoreBlocked = true;

            // Remove all other dates except this one.
            var rangesToRemove = DayRanges.Where(a => a != SelectedDayRange).ToList();

            foreach (var range in rangesToRemove)
            {
                await RemoveDayRangeModelAsync(range);
            }

            animationDirection = displayDate <= SelectedDayRange?.CalendarRenderOptions.DateRange.StartDate ?
                CalendarLoadDirection.Previous : CalendarLoadDirection.Next;
        }

        if (animationDirection == CalendarLoadDirection.Next)
        {
            foreach (var item in renderModels)
            {
                await AddDayRangeModelAsync(item);
            }
        }
        else if (animationDirection == CalendarLoadDirection.Previous)
        {
            // Wait for the animation to finish.
            // Otherwise it somehow shutters a little, which is annoying.

            // if (!removeCurrent) await Task.Delay(350);

            // Insert each render model in reverse order.
            for (int i = renderModels.Count - 1; i >= 0; i--)
            {
                await InsertDayRangeModelAsync(renderModels[i], 0);
            }
        }

        Debug.WriteLine($"Flip count: ({DayRanges.Count})");

        foreach (var item in DayRanges)
        {
            Debug.WriteLine($"- {item.CalendarRenderOptions.DateRange.ToString()}");
        }

        //if (removeCurrent)
        //{
        //    await RemoveDayRangeModelAsync(SelectedDayRange);
        //}

        // TODO...
        // await TryConsolidateItemsAsync();

        isLoadMoreBlocked = false;

        // Only scroll if the render is initiated by user.
        // Otherwise we'll scroll to the app rendered invisible date range.
        if (calendarInitInitiative == CalendarInitInitiative.User)
        {
            // Save the current settings for the page for later comparison.
            _currentDisplayType = StatePersistanceService.CalendarDisplayType;
            _displayDayCount = StatePersistanceService.DayDisplayCount;

            Messenger.Send(new ScrollToDateMessage(displayDate));
        }
    }

    private async Task InitializeCalendarEventsForDayRangeAsync(DayRangeRenderModel dayRangeRenderModel)
    {
        // Clear all events first for all days.
        foreach (var day in dayRangeRenderModel.CalendarDays)
        {
            await ExecuteUIThread(() =>
            {
                day.EventsCollection.Clear();
            });
        }

        // Initialization is done for all calendars, regardless whether they are actively selected or not.
        // This is because the filtering is cached internally of the calendar items in CalendarEventCollection.

        foreach (var calendarViewModel in AccountCalendarStateService.AllCalendars)
        {
            // Check all the events for the given date range and calendar.
            // Then find the day representation for all the events returned, and add to the collection.

            var events = await _calendarService.GetCalendarEventsAsync(calendarViewModel, dayRangeRenderModel.Period).ConfigureAwait(false);

            foreach (var @event in events)
            {
                // Find the days that the event falls into.
                var allDaysForEvent = dayRangeRenderModel.CalendarDays.Where(a => a.Period.OverlapsWith(@event.Period));

                foreach (var calendarDay in allDaysForEvent)
                {
                    var calendarItemViewModel = new CalendarItemViewModel(@event);
                    await ExecuteUIThread(() =>
                    {
                        calendarDay.EventsCollection.AddCalendarItem(calendarItemViewModel);
                    });
                }
            }
        }
    }

    private async Task TryConsolidateItemsAsync()
    {
        // Check if trimming is necessary
        if (DayRanges.Count > maxDayRangeSize)
        {
            Debug.WriteLine("Trimming items.");

            isLoadMoreBlocked = true;

            var removeCount = DayRanges.Count - maxDayRangeSize;

            await Task.Delay(500);

            // Right shifted, remove from the start.
            if (SelectedDateRangeIndex > DayRanges.Count / 2)
            {
                DayRanges.RemoveRange(DayRanges.Take(removeCount).ToList());
            }
            else
            {
                // Left shifted, remove from the end.
                DayRanges.RemoveRange(DayRanges.Skip(DayRanges.Count - removeCount).Take(removeCount));
            }

            SelectedDateRangeIndex = DayRanges.IndexOf(SelectedDayRange);
        }
    }

    private bool ShouldScrollToItem(LoadCalendarMessage message)
    {
        // Never scroll if the initiative is from the app.
        if (message.CalendarInitInitiative == CalendarInitInitiative.App) return false;

        // Nothing to scroll.
        if (DayRanges.Count == 0) return false;

        if (DayRanges.DisplayRange == null) return false;

        var selectedDate = message.DisplayDate;

        return selectedDate >= DayRanges.DisplayRange.StartDate && selectedDate <= DayRanges.DisplayRange.EndDate;
    }

    partial void OnIsAllDayChanged(bool value)
    {
        if (value)
        {
            SelectedStartTimeString = HourSelectionStrings.FirstOrDefault();
            SelectedEndTimeString = HourSelectionStrings.FirstOrDefault();
        }
        else
        {
            SelectedStartTimeString = _previousSelectedStartTimeString;
            SelectedEndTimeString = _previousSelectedEndTimeString;
        }
    }

    partial void OnSelectedStartTimeStringChanged(string oldValue, string newValue)
    {
        var parsedTime = CurrentSettings.GetTimeSpan(newValue);

        if (parsedTime == null)
        {
            SelectedStartTimeString = _previousSelectedStartTimeString;
        }
        else if (IsAllDay)
        {
            _previousSelectedStartTimeString = newValue;
        }
    }

    partial void OnSelectedEndTimeStringChanged(string oldValue, string newValue)
    {
        var parsedTime = CurrentSettings.GetTimeSpan(newValue);

        if (parsedTime == null)
        {
            SelectedEndTimeString = _previousSelectedStartTimeString;
        }
        else if (IsAllDay)
        {
            _previousSelectedEndTimeString = newValue;
        }
    }

    partial void OnSelectedDayRangeChanged(DayRangeRenderModel value)
    {
        DisplayDetailsCalendarItemViewModel = null;

        if (DayRanges.Count == 0 || SelectedDateRangeIndex < 0) return;

        var selectedRange = DayRanges[SelectedDateRangeIndex];

        Messenger.Send(new VisibleDateRangeChangedMessage(new DateRange(selectedRange.Period.Start, selectedRange.Period.End)));

        if (isLoadMoreBlocked) return;

        _ = LoadMoreAsync();
    }

    private async Task LoadMoreAsync()
    {
        try
        {
            await _calendarLoadingSemaphore.WaitAsync();

            // Depending on the selected index, we'll load more dates.
            // Day ranges may change while the async update is in progress.
            // Therefore we wait for semaphore to be released before we continue.
            // There is no need to load more if the current index is not in ideal position.

            if (SelectedDateRangeIndex == 0)
            {
                await RenderDatesAsync(CalendarInitInitiative.App, calendarLoadDirection: CalendarLoadDirection.Previous);
            }
            else if (SelectedDateRangeIndex == DayRanges.Count - 1)
            {
                await RenderDatesAsync(CalendarInitInitiative.App, calendarLoadDirection: CalendarLoadDirection.Next);
            }
        }
        catch (Exception)
        {
            Debugger.Break();
        }
        finally
        {
            _calendarLoadingSemaphore.Release();
        }
    }

    public void Receive(CalendarSettingsUpdatedMessage message)
    {
        RefreshSettings();

        // TODO: This might need throttling due to slider in the settings page for hour height.
        // or make sure the slider does not update on each tick but on focus lost.

        // Messenger.Send(new LoadCalendarMessage(DateTime.UtcNow.Date, CalendarInitInitiative.App, true));
    }

    private IEnumerable<CalendarItemViewModel> GetCalendarItems(CalendarItemViewModel calendarItemViewModel, CalendarDayModel selectedDay)
    {
        // Multi-day events, all-day events, and recurring events are rendered across multiple days.
        // We need to find all instances with the same ID across all visible date ranges.

        if (calendarItemViewModel.IsRecurringEvent || calendarItemViewModel.IsMultiDayEvent)
        {
            return DayRanges
                .SelectMany(a => a.CalendarDays)
                .Select(b => b.EventsCollection.GetCalendarItem(calendarItemViewModel.Id))
                .Where(c => c != null)
                .Cast<CalendarItemViewModel>()
                .Distinct();
        }
        else
        {
            // Single-day, non-recurring events only appear once
            return [calendarItemViewModel];
        }
    }

    private void UnselectCalendarItem(CalendarItemViewModel calendarItemViewModel, CalendarDayModel calendarDay = null)
    {
        if (calendarItemViewModel == null) return;

        var itemsToUnselect = GetCalendarItems(calendarItemViewModel, calendarDay);

        foreach (var item in itemsToUnselect)
        {
            item.IsSelected = false;
        }
    }

    private void SelectCalendarItem(CalendarItemViewModel calendarItemViewModel, CalendarDayModel calendarDay = null)
    {
        if (calendarItemViewModel == null) return;

        var itemsToSelect = GetCalendarItems(calendarItemViewModel, calendarDay);

        foreach (var item in itemsToSelect)
        {
            item.IsSelected = true;
        }
    }

    public void Receive(CalendarItemTappedMessage message)
    {
        if (message.CalendarItemViewModel == null) return;

        DisplayDetailsCalendarItemViewModel = message.CalendarItemViewModel;
    }

    public void Receive(CalendarItemDoubleTappedMessage message) => NavigateEvent(message.CalendarItemViewModel, CalendarEventTargetType.Single);

    public void Receive(CalendarItemRightTappedMessage message) { }

    protected override async void OnCalendarItemDeleted(CalendarItem calendarItem)
    {
        base.OnCalendarItemDeleted(calendarItem);

        Debug.WriteLine($"Calendar item deleted: {calendarItem.Id}");

        // Check if the deleted item is currently displayed in details view
        if (DisplayDetailsCalendarItemViewModel?.Id == calendarItem.Id)
        {
            // Clear the details view since this item was deleted
            DisplayDetailsCalendarItemViewModel = null;
        }

        // Remove the event and its occurrences from all visible date ranges.
        await ExecuteUIThread(() =>
        {
            foreach (var dayRange in DayRanges)
            {
                foreach (var calendarDay in dayRange.CalendarDays)
                {
                    var existingItem = calendarDay.EventsCollection.GetCalendarItem(calendarItem.Id);
                    if (existingItem != null)
                    {
                        calendarDay.EventsCollection.RemoveCalendarItem(existingItem);
                    }
                }
            }
        });
    }

    protected override async void OnCalendarItemUpdated(CalendarItem calendarItem)
    {
        base.OnCalendarItemUpdated(calendarItem);
        Debug.WriteLine($"Calendar item updated: {calendarItem.Id}");

        // Updated item might've been from specific time to all-day event, or vice versa.
        // Remove the event and its occurrences from all visible date ranges first.
        await ExecuteUIThread(() =>
        {
            foreach (var dayRange in DayRanges)
            {
                foreach (var calendarDay in dayRange.CalendarDays)
                {
                    var existingItem = calendarDay.EventsCollection.GetCalendarItem(calendarItem.Id);
                    if (existingItem != null)
                    {
                        calendarDay.EventsCollection.RemoveCalendarItem(existingItem);
                    }
                }
            }
        });

        // Re-add to corresponding day ranges to render properly.
        // Check if event falls into the current date range.
        if (DayRanges.DisplayRange == null) return;

        // Get all periods from the visible day ranges
        var visiblePeriods = DayRanges.Select(dr => dr.Period).ToList();

        // For recurring events, expand them to check if any occurrences fall within visible periods
        // For regular events, just check if they overlap with any period
        var matchingItems = await _calendarService.GetExpandedRecurringEventsForPeriodsAsync(calendarItem, visiblePeriods);

        foreach (var item in matchingItems)
        {
            // Find the days that the event falls into
            var allDaysForEvent = DayRanges
                .SelectMany(a => a.CalendarDays)
                .Where(a => a.Period.OverlapsWith(item.Period));

            foreach (var calendarDay in allDaysForEvent)
            {
                var calendarItemViewModel = new CalendarItemViewModel(item);

                await ExecuteUIThread(() =>
                {
                    calendarDay.EventsCollection.AddCalendarItem(calendarItemViewModel);
                });
            }
        }

        FilterActiveCalendars(DayRanges);
    }

    protected override async void OnCalendarItemAdded(CalendarItem calendarItem)
    {
        base.OnCalendarItemAdded(calendarItem);
        Debug.WriteLine($"Calendar item added: {calendarItem.Id}");

        // Check if event falls into the current date range.
        if (DayRanges.DisplayRange == null) return;

        // Check if this is a server-synced item that matches a local preview
        // Local previews don't have RemoteEventId, server-synced items do
        if (!string.IsNullOrEmpty(calendarItem.RemoteEventId))
        {
            // Find local preview items that match this event's properties
            var localPreviewItems = DayRanges
                .SelectMany(a => a.CalendarDays)
                .SelectMany(b => b.EventsCollection.RegularEvents.Concat(b.EventsCollection.AllDayEvents))
                .OfType<CalendarItemViewModel>()
                .Where(c => c.AssignedCalendar.Id == calendarItem.CalendarId &&
                           c.CalendarItem.IsLocalPreview && // Local preview (no RemoteEventId)
                           c.Title == calendarItem.Title &&
                           Math.Abs((c.StartDate - calendarItem.LocalStartDate).TotalSeconds) < 60 &&
                           Math.Abs(c.DurationInSeconds - calendarItem.DurationInSeconds) < 1)
                .ToList();

            if (localPreviewItems.Any())
            {
                Debug.WriteLine($"Found {localPreviewItems.Count} matching local preview items for {calendarItem.Title}, removing them.");

                // Remove all matching local preview items
                await ExecuteUIThread(() =>
                {
                    foreach (var dayRange in DayRanges)
                    {
                        foreach (var calendarDay in dayRange.CalendarDays)
                        {
                            foreach (var localPreview in localPreviewItems)
                            {
                                var itemInDay = calendarDay.EventsCollection.GetCalendarItem(localPreview.Id);
                                if (itemInDay != null)
                                {
                                    calendarDay.EventsCollection.RemoveCalendarItem(itemInDay);
                                }
                            }
                        }
                    }
                });
            }
        }

        // Get all periods from the visible day ranges
        var visiblePeriods = DayRanges.Select(dr => dr.Period).ToList();

        // For recurring events, expand them to check if any occurrences fall within visible periods
        // For regular events, just check if they overlap with any period
        var matchingItems = await _calendarService.GetExpandedRecurringEventsForPeriodsAsync(calendarItem, visiblePeriods);

        foreach (var item in matchingItems)
        {
            // Find the days that the event falls into
            var allDaysForEvent = DayRanges
                .SelectMany(a => a.CalendarDays)
                .Where(a => a.Period.OverlapsWith(item.Period));

            foreach (var calendarDay in allDaysForEvent)
            {
                var calendarItemViewModel = new CalendarItemViewModel(item);

                await ExecuteUIThread(() =>
                {
                    calendarDay.EventsCollection.AddCalendarItem(calendarItemViewModel);
                });
            }
        }

        FilterActiveCalendars(DayRanges);
    }
}
