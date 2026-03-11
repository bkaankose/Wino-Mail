using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Itenso.TimePeriod;
using MoreLinq;
using Serilog;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Calendar.ViewModels.Messages;
using Wino.Core.Domain;
using Wino.Core.Domain.Collections;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Calendar.CalendarTypeStrategies;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.UI;

namespace Wino.Calendar.ViewModels;

public partial class CalendarPageViewModel : CalendarBaseViewModel,
    IRecipient<LoadCalendarMessage>,
    IRecipient<CalendarItemDeleted>,
    IRecipient<CalendarSettingsUpdatedMessage>,
    IRecipient<CalendarItemTappedMessage>,
    IRecipient<CalendarItemDoubleTappedMessage>,
    IRecipient<CalendarItemRightTappedMessage>,
    IRecipient<AccountRemovedMessage>,
    IDisposable
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
    public partial CalendarOrientation CalendarOrientation { get; set; } = CalendarOrientation.Horizontal;
    [ObservableProperty]
    public partial DayRangeCollection DayRanges { get; set; } = [];
    [ObservableProperty]
    public partial int SelectedDateRangeIndex { get; set; }
    [ObservableProperty]
    public partial DayRangeRenderModel SelectedDayRange { get; set; }
    [ObservableProperty]
    public partial bool IsCalendarEnabled { get; set; } = true;

    #endregion

    #region Event Details

    public event EventHandler DetailsShowCalendarItemChanged;

    public bool CanJoinOnline => DisplayDetailsCalendarItemViewModel != null &&
                                        !string.IsNullOrEmpty(DisplayDetailsCalendarItemViewModel.CalendarItem.HtmlLink);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEventDetailsVisible))]
    [NotifyCanExecuteChangedFor(nameof(JoinOnlineCommand))]
    [NotifyPropertyChangedFor(nameof(CanJoinOnline))]
    public partial CalendarItemViewModel DisplayDetailsCalendarItemViewModel { get; set; }

    public bool IsEventDetailsVisible => DisplayDetailsCalendarItemViewModel != null;

    #endregion

    // TODO: Get rid of some of the items if we have too many.
    private const int maxDayRangeSize = 10;

    private readonly ICalendarService _calendarService;
    private readonly INavigationService _navigationService;
    private readonly IKeyPressService _keyPressService;
    private readonly INativeAppService _nativeAppService;
    private readonly IPreferencesService _preferencesService;
    private readonly IWinoRequestDelegator _winoRequestDelegator;
    private readonly IMailDialogService _dialogService;

    // Store latest rendered options.
    private CalendarDisplayType _currentDisplayType;
    private int _displayDayCount;

    private SemaphoreSlim _calendarLoadingSemaphore = new(1);
    private bool isLoadMoreBlocked = false;
    private bool _subscriptionsAttached;
    private CancellationTokenSource _pageLifetimeCts = new();
    private long _pageLifetimeVersion;

    [ObservableProperty]
    private CalendarSettings _currentSettings;

    public IStatePersistanceService StatePersistanceService { get; }
    public IAccountCalendarStateService AccountCalendarStateService { get; }

    public CalendarPageViewModel(IStatePersistanceService statePersistanceService,
                                 ICalendarService calendarService,
                                 INavigationService navigationService,
                                 IKeyPressService keyPressService,
                                 INativeAppService nativeAppService,
                                 IAccountCalendarStateService accountCalendarStateService,
                                 IPreferencesService preferencesService,
                                 IWinoRequestDelegator winoRequestDelegator,
                                 IMailDialogService dialogService)
    {
        StatePersistanceService = statePersistanceService;
        AccountCalendarStateService = accountCalendarStateService;

        _calendarService = calendarService;
        _navigationService = navigationService;
        _keyPressService = keyPressService;
        _nativeAppService = nativeAppService;
        _preferencesService = preferencesService;
        _winoRequestDelegator = winoRequestDelegator;
        _dialogService = dialogService;

    }

    public override async Task KeyboardShortcutHook(KeyboardShortcutTriggerDetails args)
    {
        if (args.Handled || args.Mode != WinoApplicationMode.Calendar || args.Action != KeyboardShortcutAction.Delete)
            return;

        if (DisplayDetailsCalendarItemViewModel?.CalendarItem == null)
            return;

        if (DisplayDetailsCalendarItemViewModel.CalendarItem.IsRecurringParent)
        {
            var confirmed = await _dialogService.ShowConfirmationDialogAsync(
                Translator.DialogMessage_DeleteRecurringSeriesMessage,
                Translator.DialogMessage_DeleteRecurringSeriesTitle,
                Translator.Buttons_Delete);

            if (!confirmed)
                return;
        }

        var preparationRequest = new CalendarOperationPreparationRequest(
            CalendarSynchronizerOperation.DeleteEvent,
            DisplayDetailsCalendarItemViewModel.CalendarItem,
            null);

        await _winoRequestDelegator.ExecuteAsync(preparationRequest);
        DisplayDetailsCalendarItemViewModel = null;
        args.Handled = true;
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        Messenger.Unregister<LoadCalendarMessage>(this);
        Messenger.Unregister<CalendarSettingsUpdatedMessage>(this);
        Messenger.Unregister<CalendarItemTappedMessage>(this);
        Messenger.Unregister<CalendarItemDoubleTappedMessage>(this);
        Messenger.Unregister<CalendarItemRightTappedMessage>(this);
        Messenger.Unregister<AccountRemovedMessage>(this);

        Messenger.Register<LoadCalendarMessage>(this);
        Messenger.Register<CalendarSettingsUpdatedMessage>(this);
        Messenger.Register<CalendarItemTappedMessage>(this);
        Messenger.Register<CalendarItemDoubleTappedMessage>(this);
        Messenger.Register<CalendarItemRightTappedMessage>(this);
        Messenger.Register<AccountRemovedMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<LoadCalendarMessage>(this);
        Messenger.Unregister<CalendarSettingsUpdatedMessage>(this);
        Messenger.Unregister<CalendarItemTappedMessage>(this);
        Messenger.Unregister<CalendarItemDoubleTappedMessage>(this);
        Messenger.Unregister<CalendarItemRightTappedMessage>(this);
        Messenger.Unregister<AccountRemovedMessage>(this);
    }

    private void AccountCalendarStateCollectivelyChanged(object sender, GroupedAccountCalendarViewModel e)
        => _ = FilterActiveCalendarsAsync(DayRanges);

    private void UpdateAccountCalendarRequested(object sender, AccountCalendarViewModel e)
        => _ = FilterActiveCalendarsAsync(DayRanges);

    private async Task FilterActiveCalendarsAsync(IEnumerable<DayRangeRenderModel> dayRangeRenderModels)
    {
        await FilterActiveCalendarsAsync(dayRangeRenderModels, CurrentPageLifetimeVersion).ConfigureAwait(false);
    }

    private async Task FilterActiveCalendarsAsync(IEnumerable<DayRangeRenderModel> dayRangeRenderModels, long lifetimeVersion)
    {
        if (dayRangeRenderModels == null || !IsPageActive(lifetimeVersion))
            return;

        await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () =>
        {
            var days = dayRangeRenderModels.SelectMany(a => a.CalendarDays);

            days.ForEach(a => a.EventsCollection.FilterByCalendars(AccountCalendarStateService.ActiveCalendars.Select(a => a.Id)));

            DisplayDetailsCalendarItemViewModel = null;
        });
    }

    [RelayCommand(CanExecute = nameof(CanJoinOnline))]
    private async Task JoinOnlineAsync()
    {
        if (DisplayDetailsCalendarItemViewModel == null || string.IsNullOrEmpty(DisplayDetailsCalendarItemViewModel.CalendarItem.HtmlLink)) return;

        await _nativeAppService.LaunchUriAsync(new Uri(DisplayDetailsCalendarItemViewModel.CalendarItem.HtmlLink));
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

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        ResetPageLifetime();
        base.OnNavigatedTo(mode, parameters);
        AttachSubscriptions();
        RefreshSettings();
        IsCalendarEnabled = true;

        if (mode == NavigationMode.Back && DayRanges.Count > 0)
        {
            RestoreVisibleState();
            _ = RefreshVisibleRangesAsync();
            return;
        }

        // Automatically select the first primary calendar for quick event dialog.
        SelectedQuickEventAccountCalendar = AccountCalendarStateService.ActiveCalendars.FirstOrDefault(a => a.IsPrimary);
    }

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        base.OnNavigatedFrom(mode, parameters);

        if (StatePersistanceService.ApplicationMode == WinoApplicationMode.Calendar)
        {
            CancelPendingOperations();
            DetachSubscriptions();
            return;
        }

        CleanupForShellDeactivation();
    }

    private void AttachSubscriptions()
    {
        if (_subscriptionsAttached)
            return;

        AccountCalendarStateService.AccountCalendarSelectionStateChanged += UpdateAccountCalendarRequested;
        AccountCalendarStateService.CollectiveAccountGroupSelectionStateChanged += AccountCalendarStateCollectivelyChanged;
        _subscriptionsAttached = true;
    }

    private void DetachSubscriptions()
    {
        if (!_subscriptionsAttached)
            return;

        AccountCalendarStateService.AccountCalendarSelectionStateChanged -= UpdateAccountCalendarRequested;
        AccountCalendarStateService.CollectiveAccountGroupSelectionStateChanged -= AccountCalendarStateCollectivelyChanged;
        _subscriptionsAttached = false;
    }

    private void ReleasePageState()
    {
        DetachSubscriptions();

        DisplayDetailsCalendarItemViewModel = null;
        SelectedQuickEventAccountCalendar = null;
        SelectedQuickEventDate = null;
        SelectedDayRange = null;
        SelectedDateRangeIndex = 0;
        IsQuickEventDialogOpen = false;
        DayRanges = [];
        HourSelectionStrings = [];
    }

    public void Dispose()
    {
        CleanupForShellDeactivation();
    }

    public void CleanupForShellDeactivation()
    {
        CancelPendingOperations();
        ReleasePageState();
        GC.SuppressFinalize(this);
    }

    public bool RestoreVisibleState()
    {
        IsCalendarEnabled = true;

        if (DayRanges.Count == 0)
        {
            SelectedDayRange = null;
            SelectedDateRangeIndex = -1;
            return false;
        }

        var targetIndex = SelectedDateRangeIndex;

        if (SelectedDayRange != null)
        {
            var existingSelectedRangeIndex = DayRanges.IndexOf(SelectedDayRange);
            if (existingSelectedRangeIndex >= 0)
            {
                targetIndex = existingSelectedRangeIndex;
            }
        }

        if (targetIndex < 0 || targetIndex >= DayRanges.Count)
        {
            targetIndex = 0;
        }

        SelectedDateRangeIndex = targetIndex;
        SelectedDayRange = DayRanges[targetIndex];

        return true;
    }

    public DateTime GetRestoreDate()
    {
        if (SelectedDayRange != null)
        {
            return SelectedDayRange.CalendarRenderOptions.DateRange.StartDate;
        }

        if (DayRanges.Count == 0)
        {
            return DateTime.Now.Date;
        }

        var targetIndex = SelectedDateRangeIndex;
        if (targetIndex < 0 || targetIndex >= DayRanges.Count)
        {
            targetIndex = 0;
        }

        return DayRanges[targetIndex].CalendarRenderOptions.DateRange.StartDate;
    }

    private long CurrentPageLifetimeVersion => Interlocked.Read(ref _pageLifetimeVersion);

    private bool IsPageActive(long lifetimeVersion)
        => lifetimeVersion == CurrentPageLifetimeVersion && !_pageLifetimeCts.IsCancellationRequested;

    private bool IsCurrentPageActive => !_pageLifetimeCts.IsCancellationRequested;

    private void ResetPageLifetime()
    {
        CancelPendingOperations();
        _pageLifetimeCts = new CancellationTokenSource();
        Interlocked.Increment(ref _pageLifetimeVersion);
    }

    private void CancelPendingOperations()
    {
        if (!_pageLifetimeCts.IsCancellationRequested)
        {
            _pageLifetimeCts.Cancel();
        }
    }

    private async Task<bool> WaitForCalendarLoadingLockAsync(long lifetimeVersion)
    {
        if (!IsPageActive(lifetimeVersion))
            return false;

        var cancellationToken = _pageLifetimeCts.Token;

        try
        {
            await _calendarLoadingSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return IsPageActive(lifetimeVersion);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void ReleaseCalendarLoadingLock()
    {
        try
        {
            _calendarLoadingSemaphore.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task ExecuteUIThreadIfActiveAsync(long lifetimeVersion, Action action)
    {
        if (action == null || !IsPageActive(lifetimeVersion))
            return;

        try
        {
            await ExecuteUIThread(() =>
            {
                if (IsPageActive(lifetimeVersion))
                {
                    action();
                }
            }).ConfigureAwait(false);
        }
        catch (COMException) when (!IsPageActive(lifetimeVersion))
        {
        }
        catch (ObjectDisposedException) when (!IsPageActive(lifetimeVersion))
        {
        }
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
            var composeResult = new CalendarEventComposeResult
            {
                CalendarId = SelectedQuickEventAccountCalendar.Id,
                AccountId = SelectedQuickEventAccountCalendar.Account.Id,
                Title = EventName,
                Location = Location ?? string.Empty,
                HtmlNotes = string.Empty,
                StartDate = startDate,
                EndDate = endDate,
                IsAllDay = IsAllDay,
                TimeZoneId = TimeZoneInfo.Local.Id,
                ShowAs = SelectedQuickEventAccountCalendar.DefaultShowAs,
                SelectedReminders = [],
                Attendees = [],
                Attachments = [],
                Recurrence = string.Empty,
                RecurrenceSummary = string.Empty
            };

            // Close dialog first
            IsQuickEventDialogOpen = false;

            var preparationRequest = new CalendarOperationPreparationRequest(
                CalendarSynchronizerOperation.CreateEvent,
                ComposeResult: composeResult);
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
        if (SelectedQuickEventDate == null)
            return;

        var startDate = SelectedQuickEventDate.Value.Date.AddHours(9);
        var endDate = startDate.AddMinutes(30);

        if (!IsAllDay)
        {
            var selectedStartTime = CurrentSettings.GetTimeSpan(SelectedStartTimeString);
            var selectedEndTime = CurrentSettings.GetTimeSpan(SelectedEndTimeString);

            if (selectedStartTime.HasValue)
            {
                startDate = SelectedQuickEventDate.Value.Date.Add(selectedStartTime.Value);
            }

            if (selectedEndTime.HasValue)
            {
                endDate = SelectedQuickEventDate.Value.Date.Add(selectedEndTime.Value);
            }
        }
        else
        {
            startDate = SelectedQuickEventDate.Value.Date;
            endDate = SelectedQuickEventDate.Value.Date.AddDays(1);
        }

        IsQuickEventDialogOpen = false;

        _navigationService.Navigate(WinoPage.CalendarEventComposePage, new CalendarEventComposeNavigationArgs
        {
            SelectedCalendarId = SelectedQuickEventAccountCalendar?.Id,
            Title = EventName ?? string.Empty,
            Location = Location ?? string.Empty,
            IsAllDay = IsAllDay,
            StartDate = startDate,
            EndDate = endDate
        });
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
        var lifetimeVersion = CurrentPageLifetimeVersion;
        var hasLoadingLock = await WaitForCalendarLoadingLockAsync(lifetimeVersion).ConfigureAwait(false);

        if (!hasLoadingLock)
            return;

        try
        {
            await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () => IsCalendarEnabled = false).ConfigureAwait(false);

            if (!IsPageActive(lifetimeVersion))
                return;

            if (!ShouldResetDayRanges(message) && ShouldScrollToItem(message))
            {
                // Scroll to the selected date.
                if (IsPageActive(lifetimeVersion))
                {
                    Messenger.Send(new ScrollToDateMessage(message.DisplayDate));
                }

                Debug.WriteLine("Scrolling to selected date.");
                return;
            }

            AdjustCalendarOrientation();

            // This will replace the whole collection because the user initiated a new render.
            await RenderDatesAsync(message.CalendarInitInitiative,
                                   message.DisplayDate,
                                   CalendarLoadDirection.Replace,
                                   lifetimeVersion).ConfigureAwait(false);

            // Scroll to the current hour.
            if (IsPageActive(lifetimeVersion))
            {
                Messenger.Send(new ScrollToHourMessage(TimeSpan.FromHours(DateTime.Now.Hour)));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (COMException) when (!IsPageActive(lifetimeVersion))
        {
        }
        catch (ObjectDisposedException) when (!IsPageActive(lifetimeVersion))
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while loading calendar.");
            Debugger.Break();
        }
        finally
        {
            ReleaseCalendarLoadingLock();

            await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () => IsCalendarEnabled = true).ConfigureAwait(false);
        }
    }


    private async Task AddDayRangeModelAsync(DayRangeRenderModel dayRangeRenderModel, long lifetimeVersion)
    {
        if (dayRangeRenderModel == null) return;

        await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () =>
        {
            DayRanges.Add(dayRangeRenderModel);
        });
    }

    private async Task InsertDayRangeModelAsync(DayRangeRenderModel dayRangeRenderModel, int index, long lifetimeVersion)
    {
        if (dayRangeRenderModel == null) return;

        await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () =>
        {
            DayRanges.Insert(index, dayRangeRenderModel);
        });
    }

    private async Task RemoveDayRangeModelAsync(DayRangeRenderModel dayRangeRenderModel, long lifetimeVersion)
    {
        if (dayRangeRenderModel == null) return;

        await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () =>
        {
            DayRanges.Remove(dayRangeRenderModel);
        });
    }

    private async Task ClearDayRangeModelsAsync(long lifetimeVersion)
    {
        await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () =>
        {
            DayRanges.Clear();
        });
    }

    private async Task ReplaceDayRangeModelsAsync(IEnumerable<DayRangeRenderModel> dayRangeRenderModels, DateTime displayDate, long lifetimeVersion)
    {
        var renderModels = dayRangeRenderModels?.ToList() ?? [];

        await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () =>
        {
            DayRanges.ReplaceRange(renderModels);

            if (renderModels.Count == 0)
            {
                SelectedDayRange = null;
                SelectedDateRangeIndex = -1;
                return;
            }

            var selectedIndex = renderModels.FindIndex(model =>
                displayDate >= model.CalendarRenderOptions.DateRange.StartDate &&
                displayDate <= model.CalendarRenderOptions.DateRange.EndDate);

            if (selectedIndex < 0)
            {
                selectedIndex = 0;
            }

            SelectedDateRangeIndex = selectedIndex;
            SelectedDayRange = renderModels[selectedIndex];
        });
    }

    private async Task RenderDatesAsync(CalendarInitInitiative calendarInitInitiative,
                                        DateTime? loadingDisplayDate = null,
                                        CalendarLoadDirection calendarLoadDirection = CalendarLoadDirection.Replace,
                                        long lifetimeVersion = 0)
    {
        if (!IsPageActive(lifetimeVersion))
            return;

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
            await InitializeCalendarEventsForDayRangeAsync(renderModel, lifetimeVersion).ConfigureAwait(false);

            if (!IsPageActive(lifetimeVersion))
                return;
        }

        // Filter by active calendars. This is a quick operation, and things are not on the UI yet.
        await FilterActiveCalendarsAsync(renderModels, lifetimeVersion).ConfigureAwait(false);

        if (!IsPageActive(lifetimeVersion))
            return;

        CalendarLoadDirection animationDirection = calendarLoadDirection;

        //bool removeCurrent = calendarLoadDirection == CalendarLoadDirection.Replace;

        if (calendarLoadDirection == CalendarLoadDirection.Replace)
        {
            isLoadMoreBlocked = true;
            await ReplaceDayRangeModelsAsync(renderModels, displayDate, lifetimeVersion).ConfigureAwait(false);
            isLoadMoreBlocked = false;

            if (calendarInitInitiative == CalendarInitInitiative.User)
            {
                _currentDisplayType = StatePersistanceService.CalendarDisplayType;
                _displayDayCount = StatePersistanceService.DayDisplayCount;

                Messenger.Send(new ScrollToDateMessage(displayDate));
            }

            return;
        }

        if (animationDirection == CalendarLoadDirection.Next)
        {
            foreach (var item in renderModels)
            {
                await AddDayRangeModelAsync(item, lifetimeVersion);
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
                await InsertDayRangeModelAsync(renderModels[i], 0, lifetimeVersion);
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

    private async Task InitializeCalendarEventsForDayRangeAsync(DayRangeRenderModel dayRangeRenderModel, long lifetimeVersion)
    {
        if (!IsPageActive(lifetimeVersion))
            return;

        // Clear all events first for all days.
        foreach (var day in dayRangeRenderModel.CalendarDays)
        {
            await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () =>
            {
                day.EventsCollection.Clear();
            });
        }

        // Initialization is done for all calendars, regardless whether they are actively selected or not.
        // This is because the filtering is cached internally of the calendar items in CalendarEventCollection.

        foreach (var calendarViewModel in AccountCalendarStateService.AllCalendars)
        {
            if (!IsPageActive(lifetimeVersion))
                return;

            // Check all the events for the given date range and calendar.
            // Then find the day representation for all the events returned, and add to the collection.

            var events = await _calendarService.GetCalendarEventsAsync(calendarViewModel, dayRangeRenderModel.Period).ConfigureAwait(false);

            foreach (var @event in events)
            {
                if (!IsPageActive(lifetimeVersion))
                    return;

                // Find the days that the event falls into.
                var allDaysForEvent = dayRangeRenderModel.CalendarDays.Where(a => a.Period.OverlapsWith(@event.Period));

                foreach (var calendarDay in allDaysForEvent)
                {
                    var calendarItemViewModel = new CalendarItemViewModel(@event);
                    await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () =>
                    {
                        calendarDay.EventsCollection.AddCalendarItem(calendarItemViewModel);
                    });
                }
            }
        }
    }

    private async Task RefreshVisibleRangesAsync()
    {
        var lifetimeVersion = CurrentPageLifetimeVersion;
        var hasLoadingLock = false;

        try
        {
            hasLoadingLock = await WaitForCalendarLoadingLockAsync(lifetimeVersion).ConfigureAwait(false);

            if (!hasLoadingLock)
                return;

            if (DayRanges == null || DayRanges.Count == 0)
                return;

            RefreshSettings();
            await RenderDatesAsync(CalendarInitInitiative.User,
                                   GetRestoreDate(),
                                   CalendarLoadDirection.Replace,
                                   lifetimeVersion).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (COMException) when (!IsPageActive(lifetimeVersion))
        {
        }
        catch (ObjectDisposedException) when (!IsPageActive(lifetimeVersion))
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh calendar ranges after navigation back.");
        }
        finally
        {
            if (hasLoadingLock)
            {
                ReleaseCalendarLoadingLock();
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
        var lifetimeVersion = CurrentPageLifetimeVersion;
        var hasLoadingLock = false;

        try
        {
            hasLoadingLock = await WaitForCalendarLoadingLockAsync(lifetimeVersion).ConfigureAwait(false);

            if (!hasLoadingLock)
                return;

            // Depending on the selected index, we'll load more dates.
            // Day ranges may change while the async update is in progress.
            // Therefore we wait for semaphore to be released before we continue.
            // There is no need to load more if the current index is not in ideal position.

            if (SelectedDateRangeIndex == 0)
            {
                await RenderDatesAsync(CalendarInitInitiative.App, calendarLoadDirection: CalendarLoadDirection.Previous, lifetimeVersion: lifetimeVersion);
            }
            else if (SelectedDateRangeIndex == DayRanges.Count - 1)
            {
                await RenderDatesAsync(CalendarInitInitiative.App, calendarLoadDirection: CalendarLoadDirection.Next, lifetimeVersion: lifetimeVersion);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (COMException) when (!IsPageActive(lifetimeVersion))
        {
        }
        catch (ObjectDisposedException) when (!IsPageActive(lifetimeVersion))
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            Debugger.Break();
        }
        finally
        {
            if (hasLoadingLock)
            {
                ReleaseCalendarLoadingLock();
            }
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

    private void UpdateCalendarItemBusyState(Guid calendarItemId, bool isBusy)
    {
        var calendarItems = DayRanges
            .SelectMany(a => a.CalendarDays)
            .Select(b => b.EventsCollection.GetCalendarItem(calendarItemId))
            .Where(c => c != null)
            .OfType<CalendarItemViewModel>()
            .Distinct();

        foreach (var item in calendarItems)
        {
            item.IsBusy = isBusy;
        }
    }

    private CalendarItemViewModel FindPendingBusyMatchByRemoteEventId(CalendarItem syncedItem)
    {
        if (syncedItem == null ||
            string.IsNullOrWhiteSpace(syncedItem.RemoteEventId) ||
            !TryExtractClientItemIdFromRemoteEventId(syncedItem.RemoteEventId, out var clientItemId))
        {
            return null;
        }

        return DayRanges
            .SelectMany(a => a.CalendarDays)
            .SelectMany(b => b.EventsCollection.RegularEvents.Concat(b.EventsCollection.AllDayEvents))
            .OfType<CalendarItemViewModel>()
            .FirstOrDefault(vm => vm.IsBusy &&
                                  vm.Id == clientItemId &&
                                  vm.AssignedCalendar?.Id == syncedItem.CalendarId);
    }

    private static bool TryExtractClientItemIdFromRemoteEventId(string remoteEventId, out Guid clientItemId)
    {
        var trackingId = remoteEventId.GetClientTrackingId();
        clientItemId = trackingId ?? Guid.Empty;
        return trackingId.HasValue;
    }

    private void RemoveCalendarItemEverywhere(Guid calendarItemId)
    {
        foreach (var dayRange in DayRanges)
        {
            foreach (var calendarDay in dayRange.CalendarDays)
            {
                var existingItem = calendarDay.EventsCollection.GetCalendarItem(calendarItemId);
                if (existingItem != null)
                {
                    calendarDay.EventsCollection.RemoveCalendarItem(existingItem);
                }
            }
        }
    }

    public void Receive(CalendarItemTappedMessage message)
    {
        if (message.CalendarItemViewModel == null) return;

        DisplayDetailsCalendarItemViewModel = message.CalendarItemViewModel;
    }

    public void Receive(CalendarItemDoubleTappedMessage message) => NavigateEvent(message.CalendarItemViewModel, CalendarEventTargetType.Single);

    public void Receive(CalendarItemRightTappedMessage message) { }

    public async void Receive(AccountRemovedMessage message)
    {
        var lifetimeVersion = CurrentPageLifetimeVersion;

        if (!IsPageActive(lifetimeVersion))
            return;

        var removedAccountId = message.Account.Id;

        await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () =>
        {
            foreach (var dayRange in DayRanges)
            {
                foreach (var calendarDay in dayRange.CalendarDays)
                {
                    calendarDay.EventsCollection.RemoveCalendarItems(item => item.AssignedCalendar?.AccountId == removedAccountId);
                }
            }

            if (DisplayDetailsCalendarItemViewModel?.AssignedCalendar?.AccountId == removedAccountId)
            {
                DisplayDetailsCalendarItemViewModel = null;
            }

            SelectedQuickEventAccountCalendar = AccountCalendarStateService.ActiveCalendars.FirstOrDefault(a => a.IsPrimary);
        });
    }

    protected override async void OnCalendarItemDeleted(CalendarItem calendarItem)
    {
        base.OnCalendarItemDeleted(calendarItem);
        var lifetimeVersion = CurrentPageLifetimeVersion;

        Debug.WriteLine($"Calendar item deleted: {calendarItem.Id}");

        // Check if the deleted item (or its series master) is currently displayed in details view.
        var isDeletedDetailsItem = DisplayDetailsCalendarItemViewModel?.Id == calendarItem.Id;
        var isDeletedSeriesMasterOfDetailsItem = DisplayDetailsCalendarItemViewModel?.CalendarItem?.RecurringCalendarItemId == calendarItem.Id;

        if (isDeletedDetailsItem || isDeletedSeriesMasterOfDetailsItem)
        {
            // Clear the details view since this item was deleted
            DisplayDetailsCalendarItemViewModel = null;
        }

        // Remove the event and its occurrences from all visible date ranges.
        await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () =>
        {
            foreach (var dayRange in DayRanges)
            {
                foreach (var calendarDay in dayRange.CalendarDays)
                {
                    calendarDay.EventsCollection.RemoveCalendarItems(item =>
                        item.Id == calendarItem.Id ||
                        (item is CalendarItemViewModel vm && vm.CalendarItem.RecurringCalendarItemId == calendarItem.Id));
                }
            }
        });
    }

    protected override async void OnCalendarItemUpdated(CalendarItem calendarItem, CalendarItemUpdateSource source)
    {
        base.OnCalendarItemUpdated(calendarItem, source);
        var lifetimeVersion = CurrentPageLifetimeVersion;
        Debug.WriteLine($"Calendar item updated: {calendarItem.Id}");

        // Local-only calendar operations are persisted immediately without real network I/O.
        // Ignore optimistic client updates to prevent applying the same mutation twice.
        var isLocalCalendarUpdate = string.IsNullOrWhiteSpace(calendarItem.RemoteEventId) ||
                                    calendarItem.RemoteEventId.StartsWith("local-", StringComparison.OrdinalIgnoreCase);
        if (isLocalCalendarUpdate && source == CalendarItemUpdateSource.ClientUpdated)
        {
            return;
        }

        // Series master events should not be visible on the UI.
        if (calendarItem.IsRecurringParent)
        {
            Debug.WriteLine($"Skipping series master event update: {calendarItem.Title}");
            return;
        }

        if (DayRanges.DisplayRange == null) return;

        // Find all days that currently have this item and days that should have it after update
        var currentDaysWithItem = DayRanges
            .SelectMany(a => a.CalendarDays)
            .Where(day => day.EventsCollection.GetCalendarItem(calendarItem.Id) != null)
            .ToList();

        var targetDaysForItem = DayRanges
            .SelectMany(a => a.CalendarDays)
            .Where(a => a.Period.OverlapsWith(calendarItem.Period))
            .ToList();

        await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () =>
        {
            if (source == CalendarItemUpdateSource.ClientUpdated)
            {
                UpdateCalendarItemBusyState(calendarItem.Id, true);
            }
            else if (source == CalendarItemUpdateSource.ClientReverted || source == CalendarItemUpdateSource.Server)
            {
                UpdateCalendarItemBusyState(calendarItem.Id, false);
            }

            // Update existing items in-place where the item should remain
            foreach (var calendarDay in currentDaysWithItem)
            {
                if (targetDaysForItem.Contains(calendarDay))
                {
                    // Item should stay in this day - update in-place
                    calendarDay.EventsCollection.UpdateCalendarItem(calendarItem);

                    if (source == CalendarItemUpdateSource.Server)
                    {
                        var existingViewModel = calendarDay.EventsCollection.GetCalendarItem(calendarItem.Id) as CalendarItemViewModel;
                        if (existingViewModel != null)
                        {
                            existingViewModel.IsBusy = false;
                        }
                    }
                }
                else
                {
                    // Item should no longer be in this day (time changed) - remove it
                    var existingItem = calendarDay.EventsCollection.GetCalendarItem(calendarItem.Id);
                    if (existingItem != null)
                    {
                        calendarDay.EventsCollection.RemoveCalendarItem(existingItem);
                    }
                }
            }

            // Add to new days where the item wasn't present before
            foreach (var calendarDay in targetDaysForItem)
            {
                if (!currentDaysWithItem.Contains(calendarDay))
                {
                    var calendarItemViewModel = new CalendarItemViewModel(calendarItem);
                    calendarDay.EventsCollection.AddCalendarItem(calendarItemViewModel);
                }
            }
        });

        await FilterActiveCalendarsAsync(DayRanges).ConfigureAwait(false);
    }

    protected override async void OnCalendarItemAdded(CalendarItem calendarItem)
    {
        base.OnCalendarItemAdded(calendarItem);
        var lifetimeVersion = CurrentPageLifetimeVersion;
        Debug.WriteLine($"Calendar item added: {calendarItem.Id}");

        // Series master events should not be visible on the UI.
        // Their instances are already expanded and synced individually.
        // For revert scenarios, restore visible child instances from local storage.
        if (calendarItem.IsRecurringParent)
        {
            Debug.WriteLine($"Skipping series master event: {calendarItem.Title}");
            await RestoreVisibleRecurringSeriesInstancesAsync(calendarItem);
            return;
        }

        // Check if event falls into the current date range.
        if (DayRanges.DisplayRange == null) return;

        // If this is server data, reconcile against optimistic client-side items first.
        // This prevents duplicate rendering when a pending busy item is replaced by the synced one.
        if (!string.IsNullOrEmpty(calendarItem.RemoteEventId))
        {
            var pendingMatch = FindPendingBusyMatchByRemoteEventId(calendarItem);

            if (pendingMatch != null)
            {
                Debug.WriteLine($"Mapped pending busy item {pendingMatch.Id} with synced server event {calendarItem.Id}.");

                await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () =>
                {
                    RemoveCalendarItemEverywhere(pendingMatch.Id);
                });
            }
        }

        // Get all periods from the visible day ranges
        // Note: Recurring event occurrences are now synced from server as individual instances
        // No local expansion needed - just check if this item overlaps with visible periods
        var allDaysForEvent = DayRanges
            .SelectMany(a => a.CalendarDays)
            .Where(a => a.Period.OverlapsWith(calendarItem.Period));

        foreach (var calendarDay in allDaysForEvent)
        {
            var calendarItemViewModel = new CalendarItemViewModel(calendarItem)
            {
                IsBusy = string.IsNullOrEmpty(calendarItem.RemoteEventId)
            };

            await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () =>
            {
                calendarDay.EventsCollection.AddCalendarItem(calendarItemViewModel);
            });
        }

        await FilterActiveCalendarsAsync(DayRanges).ConfigureAwait(false);
    }

    private async Task RestoreVisibleRecurringSeriesInstancesAsync(CalendarItem recurringParent)
    {
        var lifetimeVersion = CurrentPageLifetimeVersion;

        if (DayRanges.DisplayRange == null || recurringParent?.AssignedCalendar == null)
            return;

        var visibleRange = new TimeRange(DayRanges.DisplayRange.StartDate, DayRanges.DisplayRange.EndDate);
        var visibleItems = await _calendarService.GetCalendarEventsAsync(recurringParent.AssignedCalendar, visibleRange).ConfigureAwait(false);

        var recurringChildren = visibleItems
            .Where(item => item.RecurringCalendarItemId == recurringParent.Id && !item.IsHidden && !item.IsRecurringParent)
            .ToList();

        if (!recurringChildren.Any())
            return;

        await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () =>
        {
            foreach (var child in recurringChildren)
            {
                child.AssignedCalendar ??= recurringParent.AssignedCalendar;

                var targetDays = DayRanges
                    .SelectMany(a => a.CalendarDays)
                    .Where(day => day.Period.OverlapsWith(child.Period));

                foreach (var day in targetDays)
                {
                    if (day.EventsCollection.GetCalendarItem(child.Id) != null)
                        continue;

                    day.EventsCollection.AddCalendarItem(new CalendarItemViewModel(child)
                    {
                        IsBusy = string.IsNullOrEmpty(child.RemoteEventId)
                    });
                }
            }
        });

        await FilterActiveCalendarsAsync(DayRanges).ConfigureAwait(false);
    }
}
