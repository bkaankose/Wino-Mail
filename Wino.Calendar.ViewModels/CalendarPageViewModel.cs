using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Itenso.TimePeriod;
using Serilog;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Calendar.ViewModels.Messages;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Services;
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
    IRecipient<CalendarItemContextActionRequestedMessage>,
    IRecipient<AccountRemovedMessage>,
    IDisposable
{
    #region Quick Event Creation

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedQuickEventAccountCalendarName))]
    [NotifyCanExecuteChangedFor(nameof(SaveQuickEventCommand))]
    public partial AccountCalendarViewModel SelectedQuickEventAccountCalendar { get; set; }

    public string SelectedQuickEventAccountCalendarName
        => SelectedQuickEventAccountCalendar == null ? "Pick a calendar" : SelectedQuickEventAccountCalendar.Name;

    [ObservableProperty]
    public partial List<string> HourSelectionStrings { get; set; } = [];

    private string _previousSelectedStartTimeString = string.Empty;
    private string _previousSelectedEndTimeString = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveQuickEventCommand))]
    public partial DateTime? SelectedQuickEventDate { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveQuickEventCommand))]
    public partial bool IsAllDay { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveQuickEventCommand))]
    public partial string SelectedStartTimeString { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveQuickEventCommand))]
    public partial string SelectedEndTimeString { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Location { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveQuickEventCommand))]
    public partial string EventName { get; set; } = string.Empty;

    public DateTime QuickEventStartTime => SelectedQuickEventDate.Value.Date.Add(CurrentSettings.GetTimeSpan(SelectedStartTimeString).Value);
    public DateTime QuickEventEndTime => SelectedQuickEventDate.Value.Date.Add(CurrentSettings.GetTimeSpan(SelectedEndTimeString).Value);

    public bool CanSaveQuickEvent
    {
        get
        {
            if (SelectedQuickEventAccountCalendar == null ||
                SelectedQuickEventAccountCalendar.IsReadOnly ||
                SelectedQuickEventDate == null ||
                string.IsNullOrWhiteSpace(EventName) ||
                string.IsNullOrWhiteSpace(SelectedStartTimeString) ||
                string.IsNullOrWhiteSpace(SelectedEndTimeString))
            {
                return false;
            }

            var startTime = CurrentSettings.GetTimeSpan(SelectedStartTimeString);
            var endTime = CurrentSettings.GetTimeSpan(SelectedEndTimeString);

            if (!startTime.HasValue || !endTime.HasValue)
            {
                return false;
            }

            return IsAllDay || endTime > startTime;
        }
    }

    #endregion

    #region Visible Range

    [ObservableProperty]
    public partial VisibleDateRange CurrentVisibleRange { get; set; }

    [ObservableProperty]
    public partial string VisibleDateRangeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateRange LoadedDateWindow { get; set; }

    [ObservableProperty]
    public partial bool IsCalendarEnabled { get; set; } = true;

    [ObservableProperty]
    public partial ObservableCollection<CalendarItemViewModel> CalendarItems { get; set; } = new();

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

    private readonly ICalendarService _calendarService;
    private readonly INavigationService _navigationService;
    private readonly INativeAppService _nativeAppService;
    private readonly INotificationBuilder _notificationBuilder;
    private readonly IPreferencesService _preferencesService;
    private readonly IWinoRequestDelegator _winoRequestDelegator;
    private readonly IMailDialogService _dialogService;
    private readonly IDateContextProvider _dateContextProvider;
    private readonly ICalendarRangeTextFormatter _calendarRangeTextFormatter;

    private readonly SemaphoreSlim _calendarLoadingSemaphore = new(1);
    private bool _subscriptionsAttached;
    private CancellationTokenSource _pageLifetimeCts = new();
    private long _pageLifetimeVersion;
    private bool _isCalendarBadgeClearedForPageLifetime;
    private Dictionary<Guid, CalendarItemViewModel> _loadedCalendarItems = new();

    [ObservableProperty]
    public partial CalendarSettings CurrentSettings { get; set; }

    public IStatePersistanceService StatePersistanceService { get; }
    public IAccountCalendarStateService AccountCalendarStateService { get; }

    public CalendarPageViewModel(
        IStatePersistanceService statePersistanceService,
        ICalendarService calendarService,
        INavigationService navigationService,
        IKeyPressService keyPressService,
        INativeAppService nativeAppService,
        IAccountCalendarStateService accountCalendarStateService,
        INotificationBuilder notificationBuilder,
        IPreferencesService preferencesService,
        IWinoRequestDelegator winoRequestDelegator,
        IMailDialogService dialogService,
        IDateContextProvider dateContextProvider,
        ICalendarRangeTextFormatter calendarRangeTextFormatter)
    {
        StatePersistanceService = statePersistanceService;
        AccountCalendarStateService = accountCalendarStateService;
        _calendarService = calendarService;
        _navigationService = navigationService;
        _nativeAppService = nativeAppService;
        _notificationBuilder = notificationBuilder;
        _preferencesService = preferencesService;
        _winoRequestDelegator = winoRequestDelegator;
        _dialogService = dialogService;
        _dateContextProvider = dateContextProvider;
        _calendarRangeTextFormatter = calendarRangeTextFormatter;

        RefreshSettings();
    }

    public override async Task KeyboardShortcutHook(KeyboardShortcutTriggerDetails args)
    {
        if (args.Handled || args.Mode != WinoApplicationMode.Calendar || args.Action != KeyboardShortcutAction.Delete)
            return;

        if (DisplayDetailsCalendarItemViewModel?.CalendarItem == null)
            return;

        if (DisplayDetailsCalendarItemViewModel.AssignedCalendar?.IsReadOnly == true)
        {
            _dialogService.ShowReadOnlyCalendarMessage();
            return;
        }

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
        Messenger.Unregister<CalendarItemContextActionRequestedMessage>(this);
        Messenger.Unregister<AccountRemovedMessage>(this);

        Messenger.Register<LoadCalendarMessage>(this);
        Messenger.Register<CalendarSettingsUpdatedMessage>(this);
        Messenger.Register<CalendarItemTappedMessage>(this);
        Messenger.Register<CalendarItemDoubleTappedMessage>(this);
        Messenger.Register<CalendarItemRightTappedMessage>(this);
        Messenger.Register<CalendarItemContextActionRequestedMessage>(this);
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
        Messenger.Unregister<CalendarItemContextActionRequestedMessage>(this);
        Messenger.Unregister<AccountRemovedMessage>(this);
    }

    private void AccountCalendarStateCollectivelyChanged(object sender, GroupedAccountCalendarViewModel e)
    {
        EnsureSelectedQuickEventAccountCalendar();
        _ = ReloadCurrentVisibleRangeAsync();
    }

    private void UpdateAccountCalendarRequested(object sender, AccountCalendarViewModel e)
    {
        EnsureSelectedQuickEventAccountCalendar();
        _ = ReloadCurrentVisibleRangeAsync();
    }

    [RelayCommand(CanExecute = nameof(CanJoinOnline))]
    private async Task JoinOnlineAsync()
    {
        if (DisplayDetailsCalendarItemViewModel == null || string.IsNullOrEmpty(DisplayDetailsCalendarItemViewModel.CalendarItem.HtmlLink))
            return;

        await _nativeAppService.LaunchUriAsync(new Uri(DisplayDetailsCalendarItemViewModel.CalendarItem.HtmlLink));
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        ResetPageLifetime();
        base.OnNavigatedTo(mode, parameters);
        AttachSubscriptions();
        RefreshSettings();
        IsCalendarEnabled = true;
        EnsureSelectedQuickEventAccountCalendar();
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
        HourSelectionStrings = [];
        CurrentVisibleRange = null;
        VisibleDateRangeText = string.Empty;
        LoadedDateWindow = null;
        _loadedCalendarItems = new();
        CalendarItems = new();
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

    public bool RestoreVisibleState() => CurrentVisibleRange != null;

    public DateTime GetRestoreDate()
        => CurrentVisibleRange?.AnchorDate.ToDateTime(TimeOnly.MinValue) ?? DateTime.Now.Date;

    private long CurrentPageLifetimeVersion => Interlocked.Read(ref _pageLifetimeVersion);

    private bool IsPageActive(long lifetimeVersion)
        => lifetimeVersion == CurrentPageLifetimeVersion && !_pageLifetimeCts.IsCancellationRequested;

    private void ResetPageLifetime()
    {
        CancelPendingOperations();
        _pageLifetimeCts = new CancellationTokenSource();
        _isCalendarBadgeClearedForPageLifetime = false;
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

        try
        {
            await _calendarLoadingSemaphore.WaitAsync(_pageLifetimeCts.Token).ConfigureAwait(false);
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
        if (DisplayDetailsCalendarItemViewModel == null)
            return;

        NavigateEvent(DisplayDetailsCalendarItemViewModel, CalendarEventTargetType.Series);
    }

    [RelayCommand]
    private void NavigateEventDetails()
    {
        if (DisplayDetailsCalendarItemViewModel == null)
            return;

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
        if (SelectedQuickEventAccountCalendar?.IsReadOnly == true)
        {
            _dialogService.ShowReadOnlyCalendarMessage();
            return;
        }

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

        var preparationRequest = new CalendarOperationPreparationRequest(
            CalendarSynchronizerOperation.CreateEvent,
            ComposeResult: composeResult);
        await _winoRequestDelegator.ExecuteAsync(preparationRequest);
    }

    [RelayCommand]
    private void GoToEventComposePage()
    {
        if (SelectedQuickEventDate == null)
            return;

        var startDate = SelectedQuickEventDate.Value;
        var endDate = SelectedQuickEventDate.Value.AddMinutes(30);

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

    public async Task MoveCalendarItemAsync(CalendarItemViewModel calendarItemViewModel, DateTime targetStart)
    {
        if (calendarItemViewModel?.CalendarItem == null)
        {
            return;
        }

        var calendarItem = calendarItemViewModel.CalendarItem;

        if (!calendarItem.CanChangeStartAndEndDate)
        {
            _dialogService.InfoBarMessage(
                Translator.CalendarDragDropMoveNotAllowedTitle,
                Translator.CalendarDragDropMoveNotAllowedMessage,
                InfoBarMessageType.Warning);
            return;
        }

        if (calendarItem.AssignedCalendar?.IsReadOnly == true)
        {
            _dialogService.ShowReadOnlyCalendarMessage();
            return;
        }

        var normalizedTargetStart = calendarItem.IsAllDayEvent
            ? targetStart.Date
            : targetStart;
        var targetEnd = normalizedTargetStart.AddSeconds(calendarItem.DurationInSeconds);
        var currentLocalStart = calendarItem.LocalStartDate;
        var currentLocalEnd = calendarItem.LocalEndDate;

        if (currentLocalStart == normalizedTargetStart && currentLocalEnd == targetEnd)
        {
            return;
        }

        var originalItem = CloneCalendarItem(calendarItem);
        var attendees = await _calendarService.GetAttendeesAsync(calendarItem.EventTrackingId).ConfigureAwait(false) ?? [];
        var originalAttendees = CloneAttendees(attendees);

        await ExecuteUIThread(() =>
        {
            calendarItemViewModel.StartDate = normalizedTargetStart;
            calendarItemViewModel.DurationInSeconds = calendarItem.DurationInSeconds;
        }).ConfigureAwait(false);

        await _calendarService.UpdateCalendarItemAsync(calendarItem, attendees).ConfigureAwait(false);

        var preparationRequest = new CalendarOperationPreparationRequest(
            CalendarSynchronizerOperation.ChangeStartAndEndDate,
            calendarItem,
            attendees,
            ResponseMessage: null,
            OriginalItem: originalItem,
            OriginalAttendees: originalAttendees);

        await _winoRequestDelegator.ExecuteAsync(preparationRequest).ConfigureAwait(false);
    }

    partial void OnDisplayDetailsCalendarItemViewModelChanged(CalendarItemViewModel value)
        => DetailsShowCalendarItemChanged?.Invoke(this, EventArgs.Empty);

    private void RefreshSettings()
    {
        CurrentSettings = _preferencesService.GetCurrentCalendarSettings();

        var timeStrings = new List<string>();
        for (int hour = 0; hour < 24; hour++)
        {
            for (int minute = 0; minute < 60; minute += 30)
            {
                var time = new DateTime(1, 1, 1, hour, minute, 0);
                timeStrings.Add(CurrentSettings.DayHeaderDisplayType == DayHeaderDisplayType.TwentyFourHour
                    ? time.ToString("HH:mm")
                    : time.ToString("h:mm tt"));
            }
        }

        HourSelectionStrings = timeStrings;

        if (CurrentVisibleRange != null)
        {
            VisibleDateRangeText = _calendarRangeTextFormatter.Format(CurrentVisibleRange, _dateContextProvider);
        }
    }

    public async Task ApplyDisplayRequestAsync(CalendarDisplayRequest request, bool forceReload = false, CalendarItemTarget pendingTarget = null)
    {
        var lifetimeVersion = CurrentPageLifetimeVersion;
        var hasLoadingLock = await WaitForCalendarLoadingLockAsync(lifetimeVersion).ConfigureAwait(false);
        var loadSucceeded = false;

        if (!hasLoadingLock)
            return;

        try
        {
            await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () => IsCalendarEnabled = false).ConfigureAwait(false);

            if (!IsPageActive(lifetimeVersion))
                return;

            var currentSettings = CurrentSettings;
            if (currentSettings == null)
            {
                RefreshSettings();
                currentSettings = CurrentSettings;
            }

            var today = _dateContextProvider.GetToday();
            var visibleRange = CalendarRangeResolver.Resolve(request, currentSettings, today);
            var previousRange = CalendarRangeResolver.Navigate(visibleRange, -1, currentSettings, today);
            var nextRange = CalendarRangeResolver.Navigate(visibleRange, 1, currentSettings, today);
            var loadedDateWindow = new DateRange(
                previousRange.StartDate.ToDateTime(TimeOnly.MinValue),
                nextRange.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue));

            var shouldReload = forceReload || !IsSameVisibleRange(CurrentVisibleRange, visibleRange) || !IsSameDateRange(LoadedDateWindow, loadedDateWindow);
            List<CalendarItemViewModel> loadedItems = null;

            if (shouldReload)
            {
                loadedItems = await LoadCalendarItemsAsync(loadedDateWindow, lifetimeVersion).ConfigureAwait(false);
                if (!IsPageActive(lifetimeVersion))
                    return;
            }

            await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () =>
            {
                if (loadedItems != null)
                {
                    ReplaceLoadedCalendarItems(loadedItems);
                }

                EnsureSelectedQuickEventAccountCalendar();
                CurrentVisibleRange = visibleRange;
                LoadedDateWindow = loadedDateWindow;
                VisibleDateRangeText = _calendarRangeTextFormatter.Format(visibleRange, _dateContextProvider);
                if (DisplayDetailsCalendarItemViewModel != null && !IsCalendarActive(DisplayDetailsCalendarItemViewModel.AssignedCalendar?.Id))
                {
                    DisplayDetailsCalendarItemViewModel = null;
                }
            }).ConfigureAwait(false);

            loadSucceeded = true;
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
            Log.Error(ex, "Error while loading visible calendar range.");
        }
        finally
        {
            ReleaseCalendarLoadingLock();
            await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () => IsCalendarEnabled = true).ConfigureAwait(false);
        }

        if (loadSucceeded && !_isCalendarBadgeClearedForPageLifetime && IsPageActive(lifetimeVersion))
        {
            await _notificationBuilder.ClearCalendarTaskbarBadgeAsync().ConfigureAwait(false);
            _isCalendarBadgeClearedForPageLifetime = true;
        }

        if (loadSucceeded && pendingTarget != null && IsPageActive(lifetimeVersion))
        {
            await NavigateToPendingCalendarTargetAsync(pendingTarget).ConfigureAwait(false);
        }
    }

    public Task ReloadCurrentVisibleRangeAsync()
    {
        if (CurrentVisibleRange == null)
            return Task.CompletedTask;

        RefreshSettings();
        return ApplyDisplayRequestAsync(new CalendarDisplayRequest(CurrentVisibleRange.DisplayType, CurrentVisibleRange.AnchorDate), forceReload: true);
    }

    public async Task<IReadOnlyList<CalendarItem>> SearchCalendarItemsAsync(string queryText, int limit, CancellationToken cancellationToken)
    {
        var results = await _calendarService.SearchCalendarItemsAsync(queryText, limit, cancellationToken).ConfigureAwait(false);
        var activeCalendarIds = AccountCalendarStateService.ActiveCalendars.Select(calendar => calendar.Id).ToHashSet();

        return results
            .Where(result => activeCalendarIds.Contains(result.CalendarId))
            .ToList();
    }

    public void OpenCalendarSearchResult(CalendarItem calendarItem)
    {
        ArgumentNullException.ThrowIfNull(calendarItem);
        NavigateEvent(new CalendarItemViewModel(calendarItem), CalendarEventTargetType.Single);
    }

    private async Task NavigateToPendingCalendarTargetAsync(CalendarItemTarget target)
    {
        CalendarItemViewModel calendarItemViewModel = null;

        if (_loadedCalendarItems.TryGetValue(target.Item.Id, out var loadedCalendarItemViewModel))
        {
            calendarItemViewModel = loadedCalendarItemViewModel;
        }
        else
        {
            var targetItem = await _calendarService.GetCalendarItemTargetAsync(target).ConfigureAwait(false);
            if (targetItem == null)
                return;

            targetItem.AssignedCalendar ??= AccountCalendarStateService.ActiveCalendars.FirstOrDefault(calendar => calendar.Id == targetItem.CalendarId);
            calendarItemViewModel = new CalendarItemViewModel(targetItem);
        }

        await ExecuteUIThread(() =>
        {
            DisplayDetailsCalendarItemViewModel = calendarItemViewModel;
            NavigateEvent(calendarItemViewModel, target.TargetType);
        }).ConfigureAwait(false);
    }

    private async Task<List<CalendarItemViewModel>> LoadCalendarItemsAsync(DateRange loadedDateWindow, long lifetimeVersion)
    {
        var loadedItems = new Dictionary<Guid, CalendarItemViewModel>();
        var loadPeriod = new TimeRange(loadedDateWindow.StartDate, loadedDateWindow.EndDate);
        var activeCalendars = AccountCalendarStateService.ActiveCalendars.ToList();
        var pendingCalendarItemIds = await GetPendingCalendarItemIdsAsync(activeCalendars, lifetimeVersion).ConfigureAwait(false);

        foreach (var calendarViewModel in activeCalendars)
        {
            if (!IsPageActive(lifetimeVersion))
                return [];

            var events = await _calendarService.GetCalendarEventsAsync(calendarViewModel, loadPeriod).ConfigureAwait(false);
            foreach (var calendarItem in events)
            {
                if (calendarItem.IsRecurringParent || calendarItem.IsHidden)
                    continue;

                calendarItem.AssignedCalendar ??= calendarViewModel;

                if (!loadedItems.ContainsKey(calendarItem.Id))
                {
                    loadedItems.Add(calendarItem.Id, CreateCalendarItemViewModel(calendarItem, pendingCalendarItemIds));
                }
            }
        }

        return loadedItems.Values
            .OrderBy(item => item.StartDate)
            .ThenBy(item => item.EndDate)
            .ThenBy(item => item.Id)
            .ToList();
    }

    private static bool IsSameVisibleRange(VisibleDateRange current, VisibleDateRange next)
    {
        if (current == null && next == null)
            return true;

        if (current == null || next == null)
            return false;

        return current.DisplayType == next.DisplayType &&
               current.AnchorDate == next.AnchorDate &&
               current.StartDate == next.StartDate &&
               current.EndDate == next.EndDate;
    }

    private static bool IsSameDateRange(DateRange current, DateRange next)
    {
        if (current == null && next == null)
            return true;

        if (current == null || next == null)
            return false;

        return current.StartDate == next.StartDate && current.EndDate == next.EndDate;
    }

    private bool IsCalendarActive(Guid? calendarId)
        => calendarId.HasValue && AccountCalendarStateService.ActiveCalendars.Any(calendar => calendar.Id == calendarId.Value);

    private void EnsureSelectedQuickEventAccountCalendar()
    {
        if (SelectedQuickEventAccountCalendar != null && IsCalendarActive(SelectedQuickEventAccountCalendar.Id))
        {
            return;
        }

        SelectedQuickEventAccountCalendar = AccountCalendarStateService.ActiveCalendars.FirstOrDefault(a => a.IsPrimary)
                                            ?? AccountCalendarStateService.ActiveCalendars.FirstOrDefault();
    }

    public async void Receive(LoadCalendarMessage message)
        => await ApplyDisplayRequestAsync(message.DisplayRequest, message.ForceReload, message.PendingTarget);

    public void Receive(CalendarSettingsUpdatedMessage message)
    {
        RefreshSettings();
        _ = ReloadCurrentVisibleRangeAsync();
    }

    public void Receive(CalendarItemTappedMessage message)
    {
        if (message.CalendarItemViewModel == null)
            return;

        DisplayDetailsCalendarItemViewModel = message.CalendarItemViewModel;
    }

    public void Receive(CalendarItemDoubleTappedMessage message)
        => NavigateEvent(message.CalendarItemViewModel, CalendarEventTargetType.Single);

    public void Receive(CalendarItemRightTappedMessage message)
    {
        if (message.CalendarItemViewModel == null)
            return;

        DisplayDetailsCalendarItemViewModel = message.CalendarItemViewModel;
    }

    public void Receive(CalendarItemContextActionRequestedMessage message)
    {
        if (message.CalendarItemViewModel == null)
            return;

        if (message.Action.ActionType == CalendarContextMenuActionType.Open)
        {
            NavigateEvent(message.CalendarItemViewModel, message.Action.TargetType ?? CalendarEventTargetType.Single);
            return;
        }

        _ = ExecuteContextActionAsync(message.CalendarItemViewModel, message.Action);
    }

    public async void Receive(AccountRemovedMessage message)
    {
        if (DisplayDetailsCalendarItemViewModel?.AssignedCalendar?.AccountId == message.Account.Id)
        {
            DisplayDetailsCalendarItemViewModel = null;
        }

        EnsureSelectedQuickEventAccountCalendar();
        await ReloadCurrentVisibleRangeAsync().ConfigureAwait(false);
    }

    protected override void OnCalendarItemDeleted(CalendarItem calendarItem, EntityUpdateSource source)
    {
        base.OnCalendarItemDeleted(calendarItem, source);

        if (calendarItem == null)
            return;

        if (calendarItem.IsRecurringParent)
        {
            _ = ReloadCurrentVisibleRangeAsync();
            return;
        }

        var existingItemId = FindLoadedCalendarItemId(calendarItem);
        if (!existingItemId.HasValue)
            return;

        RemoveLoadedCalendarItem(existingItemId.Value, calendarItem);
    }

    protected override void OnCalendarItemUpdated(CalendarItem calendarItem, EntityUpdateSource source)
    {
        base.OnCalendarItemUpdated(calendarItem, source);
        ApplyCalendarItemUpsert(calendarItem, source);
    }

    protected override void OnCalendarItemAdded(CalendarItem calendarItem, EntityUpdateSource source)
    {
        base.OnCalendarItemAdded(calendarItem, source);
        ApplyCalendarItemUpsert(calendarItem, source);
    }

    private async Task<HashSet<Guid>> GetPendingCalendarItemIdsAsync(IEnumerable<AccountCalendarViewModel> activeCalendars, long lifetimeVersion)
    {
        var pendingCalendarItemIds = new HashSet<Guid>();
        var accountIds = activeCalendars
            .Select(calendar => calendar.Account.Id)
            .Where(accountId => accountId != Guid.Empty)
            .Distinct()
            .ToList();

        foreach (var accountId in accountIds)
        {
            if (!IsPageActive(lifetimeVersion))
                return pendingCalendarItemIds;

            IWinoSynchronizerBase synchronizer;
            try
            {
                synchronizer = await SynchronizationManager.Instance.GetSynchronizerAsync(accountId).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return pendingCalendarItemIds;
            }

            if (synchronizer == null)
                continue;

            foreach (var pendingCalendarItemId in synchronizer.GetPendingCalendarOperationIds())
            {
                pendingCalendarItemIds.Add(pendingCalendarItemId);
            }
        }

        return pendingCalendarItemIds;
    }

    private void ApplyCalendarItemUpsert(CalendarItem calendarItem, EntityUpdateSource source)
    {
        if (calendarItem == null)
            return;

        if (calendarItem.IsRecurringParent)
        {
            _ = ReloadCurrentVisibleRangeAsync();
            return;
        }

        var existingItemId = FindLoadedCalendarItemId(calendarItem);
        var shouldDisplay = ShouldDisplayCalendarItem(calendarItem);

        if (!shouldDisplay)
        {
            if (existingItemId.HasValue)
            {
                RemoveLoadedCalendarItem(existingItemId.Value, calendarItem);
            }

            return;
        }

        var newViewModel = CreateCalendarItemViewModel(calendarItem, source);

        if (existingItemId.HasValue)
        {
            ReplaceLoadedCalendarItem(existingItemId.Value, newViewModel);
        }
        else
        {
            InsertLoadedCalendarItem(newViewModel);
        }
    }

    private CalendarItemViewModel CreateCalendarItemViewModel(CalendarItem calendarItem, EntityUpdateSource source)
        => CreateCalendarItemViewModel(
            calendarItem,
            source == EntityUpdateSource.ClientUpdated ? new HashSet<Guid> { calendarItem.Id } : null,
            source);

    private static CalendarItem CloneCalendarItem(CalendarItem calendarItem)
        => new()
        {
            Id = calendarItem.Id,
            RemoteEventId = calendarItem.RemoteEventId,
            Title = calendarItem.Title,
            Description = calendarItem.Description,
            Location = calendarItem.Location,
            StartDate = calendarItem.StartDate,
            StartTimeZone = calendarItem.StartTimeZone,
            EndTimeZone = calendarItem.EndTimeZone,
            DurationInSeconds = calendarItem.DurationInSeconds,
            Recurrence = calendarItem.Recurrence,
            OrganizerDisplayName = calendarItem.OrganizerDisplayName,
            OrganizerEmail = calendarItem.OrganizerEmail,
            RecurringCalendarItemId = calendarItem.RecurringCalendarItemId,
            IsLocked = calendarItem.IsLocked,
            IsHidden = calendarItem.IsHidden,
            CustomEventColorHex = calendarItem.CustomEventColorHex,
            HtmlLink = calendarItem.HtmlLink,
            SnoozedUntil = calendarItem.SnoozedUntil,
            Status = calendarItem.Status,
            Visibility = calendarItem.Visibility,
            ShowAs = calendarItem.ShowAs,
            CreatedAt = calendarItem.CreatedAt,
            UpdatedAt = calendarItem.UpdatedAt,
            CalendarId = calendarItem.CalendarId,
            AssignedCalendar = calendarItem.AssignedCalendar
        };

    private static List<CalendarEventAttendee> CloneAttendees(IEnumerable<CalendarEventAttendee> attendees)
        => attendees?.Select(attendee => new CalendarEventAttendee
        {
            Id = attendee.Id,
            CalendarItemId = attendee.CalendarItemId,
            Name = attendee.Name,
            Email = attendee.Email,
            AttendenceStatus = attendee.AttendenceStatus,
            IsOrganizer = attendee.IsOrganizer,
            IsOptionalAttendee = attendee.IsOptionalAttendee,
            Comment = attendee.Comment,
            ResolvedContact = attendee.ResolvedContact
        }).ToList() ?? [];

    private CalendarItemViewModel CreateCalendarItemViewModel(CalendarItem calendarItem, ISet<Guid> pendingCalendarItemIds, EntityUpdateSource source = EntityUpdateSource.Server)
    {
        calendarItem.AssignedCalendar ??= ResolveAssignedCalendar(calendarItem.CalendarId);

        return new CalendarItemViewModel(calendarItem)
        {
            IsBusy = source == EntityUpdateSource.ClientUpdated || HasPendingCalendarOperation(calendarItem, pendingCalendarItemIds)
        };
    }

    private void ReplaceLoadedCalendarItems(IEnumerable<CalendarItemViewModel> loadedItems)
    {
        var loadedItemsList = loadedItems?.ToList() ?? [];
        CalendarItems = new ObservableCollection<CalendarItemViewModel>(loadedItemsList);
        _loadedCalendarItems = loadedItemsList.ToDictionary(item => item.Id);
    }

    private void InsertLoadedCalendarItem(CalendarItemViewModel calendarItemViewModel)
    {
        var insertionIndex = 0;

        while (insertionIndex < CalendarItems.Count && CompareCalendarItems(CalendarItems[insertionIndex], calendarItemViewModel) <= 0)
        {
            insertionIndex++;
        }

        CalendarItems.Insert(insertionIndex, calendarItemViewModel);
        _loadedCalendarItems[calendarItemViewModel.Id] = calendarItemViewModel;

        if (IsDisplayDetailsMatch(calendarItemViewModel.CalendarItem))
        {
            DisplayDetailsCalendarItemViewModel = calendarItemViewModel;
        }
    }

    private void ReplaceLoadedCalendarItem(Guid existingItemId, CalendarItemViewModel replacementViewModel)
    {
        if (!_loadedCalendarItems.TryGetValue(existingItemId, out var existingViewModel))
        {
            InsertLoadedCalendarItem(replacementViewModel);
            return;
        }

        replacementViewModel.IsSelected = existingViewModel.IsSelected;

        var existingIndex = CalendarItems.IndexOf(existingViewModel);
        if (existingIndex >= 0)
        {
            CalendarItems[existingIndex] = replacementViewModel;
        }

        _loadedCalendarItems.Remove(existingItemId);
        _loadedCalendarItems[replacementViewModel.Id] = replacementViewModel;

        if (existingIndex >= 0)
        {
            MoveCalendarItemToSortedPosition(replacementViewModel, existingIndex);
        }

        if (IsDisplayDetailsMatch(replacementViewModel.CalendarItem, existingItemId))
        {
            DisplayDetailsCalendarItemViewModel = replacementViewModel;
        }
    }

    private void RemoveLoadedCalendarItem(Guid existingItemId, CalendarItem calendarItem)
    {
        if (_loadedCalendarItems.TryGetValue(existingItemId, out var existingViewModel))
        {
            CalendarItems.Remove(existingViewModel);
            _loadedCalendarItems.Remove(existingItemId);
        }

        if (IsDisplayDetailsMatch(calendarItem, existingItemId))
        {
            DisplayDetailsCalendarItemViewModel = null;
        }
    }

    private void MoveCalendarItemToSortedPosition(CalendarItemViewModel calendarItemViewModel, int previousIndex)
    {
        if (previousIndex < 0)
            return;

        var targetIndex = 0;
        while (targetIndex < CalendarItems.Count && CompareCalendarItems(CalendarItems[targetIndex], calendarItemViewModel) <= 0)
        {
            targetIndex++;
        }

        if (targetIndex > previousIndex)
        {
            targetIndex--;
        }

        if (targetIndex != previousIndex)
        {
            CalendarItems.Move(previousIndex, targetIndex);
        }
    }

    private Guid? FindLoadedCalendarItemId(CalendarItem calendarItem)
    {
        if (calendarItem == null)
            return null;

        if (_loadedCalendarItems.ContainsKey(calendarItem.Id))
            return calendarItem.Id;

        var trackedLocalItemId = calendarItem.RemoteEventId.GetClientTrackingId();
        if (trackedLocalItemId.HasValue && _loadedCalendarItems.ContainsKey(trackedLocalItemId.Value))
            return trackedLocalItemId.Value;

        return null;
    }

    private bool ShouldDisplayCalendarItem(CalendarItem calendarItem)
    {
        if (calendarItem == null || LoadedDateWindow == null)
            return false;

        if (calendarItem.IsHidden || calendarItem.IsRecurringParent || !IsCalendarActive(calendarItem.CalendarId))
            return false;

        var loadedWindow = new TimeRange(LoadedDateWindow.StartDate, LoadedDateWindow.EndDate);
        return loadedWindow.OverlapsWith(calendarItem.Period);
    }

    private bool IsDisplayDetailsMatch(CalendarItem calendarItem, Guid? existingItemId = null)
    {
        if (DisplayDetailsCalendarItemViewModel == null || calendarItem == null)
            return false;

        var trackedLocalItemId = calendarItem.RemoteEventId.GetClientTrackingId();

        return DisplayDetailsCalendarItemViewModel.Id == calendarItem.Id ||
               (existingItemId.HasValue && DisplayDetailsCalendarItemViewModel.Id == existingItemId.Value) ||
               (trackedLocalItemId.HasValue && DisplayDetailsCalendarItemViewModel.Id == trackedLocalItemId.Value) ||
               DisplayDetailsCalendarItemViewModel.CalendarItem?.RecurringCalendarItemId == calendarItem.Id;
    }

    private bool HasPendingCalendarOperation(CalendarItem calendarItem, ISet<Guid> pendingCalendarItemIds)
    {
        if (calendarItem == null || pendingCalendarItemIds == null || pendingCalendarItemIds.Count == 0)
            return false;

        if (pendingCalendarItemIds.Contains(calendarItem.Id))
            return true;

        var trackedLocalItemId = calendarItem.RemoteEventId.GetClientTrackingId();
        return trackedLocalItemId.HasValue && pendingCalendarItemIds.Contains(trackedLocalItemId.Value);
    }

    private async Task ExecuteContextActionAsync(CalendarItemViewModel calendarItemViewModel, CalendarContextMenuAction action)
    {
        switch (action.ActionType)
        {
            case CalendarContextMenuActionType.JoinOnline:
                await JoinOnlineAsync(calendarItemViewModel).ConfigureAwait(false);
                break;
            case CalendarContextMenuActionType.Delete:
                await DeleteCalendarItemAsync(calendarItemViewModel, action.TargetType ?? CalendarEventTargetType.Single).ConfigureAwait(false);
                break;
            case CalendarContextMenuActionType.ShowAs when action.ShowAs.HasValue:
                await UpdateShowAsAsync(calendarItemViewModel, action.TargetType ?? CalendarEventTargetType.Single, action.ShowAs.Value).ConfigureAwait(false);
                break;
            case CalendarContextMenuActionType.Respond when action.ResponseStatus.HasValue:
                await RespondToCalendarItemAsync(calendarItemViewModel, action.TargetType ?? CalendarEventTargetType.Single, action.ResponseStatus.Value).ConfigureAwait(false);
                break;
        }
    }

    private Task JoinOnlineAsync(CalendarItemViewModel calendarItemViewModel)
    {
        var htmlLink = calendarItemViewModel?.CalendarItem?.HtmlLink;

        if (string.IsNullOrWhiteSpace(htmlLink))
            return Task.CompletedTask;

        return _nativeAppService.LaunchUriAsync(new Uri(htmlLink));
    }

    private async Task DeleteCalendarItemAsync(CalendarItemViewModel calendarItemViewModel, CalendarEventTargetType targetType)
    {
        var targetItem = await ResolveCalendarItemTargetAsync(calendarItemViewModel, targetType).ConfigureAwait(false);

        if (targetItem == null)
            return;

        if (targetItem.AssignedCalendar?.IsReadOnly == true)
        {
            _dialogService.ShowReadOnlyCalendarMessage();
            return;
        }

        if (targetItem.IsRecurringParent)
        {
            var confirmed = await _dialogService.ShowConfirmationDialogAsync(
                Translator.DialogMessage_DeleteRecurringSeriesMessage,
                Translator.DialogMessage_DeleteRecurringSeriesTitle,
                Translator.Buttons_Delete).ConfigureAwait(false);

            if (!confirmed)
                return;
        }

        var preparationRequest = new CalendarOperationPreparationRequest(
            CalendarSynchronizerOperation.DeleteEvent,
            targetItem,
            null);

        await _winoRequestDelegator.ExecuteAsync(preparationRequest).ConfigureAwait(false);
    }

    private async Task UpdateShowAsAsync(CalendarItemViewModel calendarItemViewModel, CalendarEventTargetType targetType, CalendarItemShowAs showAs)
    {
        var targetItem = await ResolveCalendarItemTargetAsync(calendarItemViewModel, targetType).ConfigureAwait(false);

        if (targetItem == null || targetItem.ShowAs == showAs)
            return;

        if (targetItem.AssignedCalendar?.IsReadOnly == true)
        {
            _dialogService.ShowReadOnlyCalendarMessage();
            return;
        }

        var originalItem = await _calendarService.GetCalendarItemAsync(targetItem.Id).ConfigureAwait(false);
        var attendees = await _calendarService.GetAttendeesAsync(targetItem.Id).ConfigureAwait(false);

        targetItem.ShowAs = showAs;
        await _calendarService.UpdateCalendarItemAsync(targetItem, attendees).ConfigureAwait(false);

        var preparationRequest = new CalendarOperationPreparationRequest(
            CalendarSynchronizerOperation.UpdateEvent,
            targetItem,
            attendees,
            ResponseMessage: null,
            OriginalItem: originalItem,
            OriginalAttendees: attendees);

        await _winoRequestDelegator.ExecuteAsync(preparationRequest).ConfigureAwait(false);
    }

    private async Task RespondToCalendarItemAsync(CalendarItemViewModel calendarItemViewModel, CalendarEventTargetType targetType, CalendarItemStatus responseStatus)
    {
        var targetItem = await ResolveCalendarItemTargetAsync(calendarItemViewModel, targetType).ConfigureAwait(false);

        if (targetItem == null)
            return;

        if (targetItem.AssignedCalendar?.IsReadOnly == true)
        {
            _dialogService.ShowReadOnlyCalendarMessage();
            return;
        }

        var operation = responseStatus switch
        {
            CalendarItemStatus.Accepted => CalendarSynchronizerOperation.AcceptEvent,
            CalendarItemStatus.Tentative => CalendarSynchronizerOperation.TentativeEvent,
            CalendarItemStatus.Cancelled => CalendarSynchronizerOperation.DeclineEvent,
            _ => throw new InvalidOperationException($"Unsupported calendar response status '{responseStatus}'.")
        };

        var preparationRequest = new CalendarOperationPreparationRequest(
            operation,
            targetItem,
            null);

        await _winoRequestDelegator.ExecuteAsync(preparationRequest).ConfigureAwait(false);
    }

    private async Task<CalendarItem> ResolveCalendarItemTargetAsync(CalendarItemViewModel calendarItemViewModel, CalendarEventTargetType targetType)
    {
        if (calendarItemViewModel?.CalendarItem == null)
            return null;

        var target = new CalendarItemTarget(calendarItemViewModel.CalendarItem, targetType);
        var targetItem = await _calendarService.GetCalendarItemTargetAsync(target).ConfigureAwait(false);

        targetItem ??= calendarItemViewModel.CalendarItem;
        if (targetItem == calendarItemViewModel.CalendarItem || targetItem.AssignedCalendar == null)
        {
            targetItem.AssignedCalendar = await _calendarService.GetAccountCalendarAsync(targetItem.CalendarId).ConfigureAwait(false)
                                        ?? calendarItemViewModel.AssignedCalendar
                                        ?? ResolveAssignedCalendar(targetItem.CalendarId);
        }

        return targetItem;
    }

    private AccountCalendarViewModel ResolveAssignedCalendar(Guid calendarId)
        => AccountCalendarStateService.AllCalendars.FirstOrDefault(calendar => calendar.Id == calendarId);

    private static int CompareCalendarItems(CalendarItemViewModel left, CalendarItemViewModel right)
    {
        var compareResult = DateTime.Compare(left?.StartDate ?? DateTime.MinValue, right?.StartDate ?? DateTime.MinValue);
        if (compareResult != 0)
            return compareResult;

        compareResult = DateTime.Compare(left?.EndDate ?? DateTime.MinValue, right?.EndDate ?? DateTime.MinValue);
        if (compareResult != 0)
            return compareResult;

        return Nullable.Compare(left?.Id, right?.Id);
    }

    partial void OnIsAllDayChanged(bool value)
    {
        if (value)
        {
            _previousSelectedStartTimeString = SelectedStartTimeString;
            _previousSelectedEndTimeString = SelectedEndTimeString;
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
        else if (!IsAllDay)
        {
            _previousSelectedStartTimeString = newValue;
        }
    }

    partial void OnSelectedEndTimeStringChanged(string oldValue, string newValue)
    {
        var parsedTime = CurrentSettings.GetTimeSpan(newValue);

        if (parsedTime == null)
        {
            SelectedEndTimeString = _previousSelectedEndTimeString;
        }
        else if (!IsAllDay)
        {
            _previousSelectedEndTimeString = newValue;
        }
    }
}
