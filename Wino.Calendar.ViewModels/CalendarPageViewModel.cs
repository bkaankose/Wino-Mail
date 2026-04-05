using System;
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
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Calendar;
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
    public partial IReadOnlyList<CalendarItemViewModel> CalendarItems { get; set; } = [];

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
    private readonly IPreferencesService _preferencesService;
    private readonly IWinoRequestDelegator _winoRequestDelegator;
    private readonly IMailDialogService _dialogService;
    private readonly IDateContextProvider _dateContextProvider;
    private readonly ICalendarRangeTextFormatter _calendarRangeTextFormatter;

    private readonly SemaphoreSlim _calendarLoadingSemaphore = new(1);
    private bool _subscriptionsAttached;
    private CancellationTokenSource _pageLifetimeCts = new();
    private long _pageLifetimeVersion;
    private List<CalendarItemViewModel> _loadedCalendarItems = [];

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
        _loadedCalendarItems = [];
        CalendarItems = [];
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

    public async Task ApplyDisplayRequestAsync(CalendarDisplayRequest request, bool forceReload = false)
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
                    _loadedCalendarItems = loadedItems;
                    CalendarItems = loadedItems;
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
    }

    public Task ReloadCurrentVisibleRangeAsync()
    {
        if (CurrentVisibleRange == null)
            return Task.CompletedTask;

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

    private async Task<List<CalendarItemViewModel>> LoadCalendarItemsAsync(DateRange loadedDateWindow, long lifetimeVersion)
    {
        var loadedItems = new Dictionary<Guid, CalendarItemViewModel>();
        var loadPeriod = new TimeRange(loadedDateWindow.StartDate, loadedDateWindow.EndDate);

        foreach (var calendarViewModel in AccountCalendarStateService.ActiveCalendars)
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
                    loadedItems.Add(calendarItem.Id, new CalendarItemViewModel(calendarItem));
                }
            }
        }

        return loadedItems.Values.ToList();
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
        => await ApplyDisplayRequestAsync(message.DisplayRequest, message.ForceReload);

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

    protected override void OnCalendarItemDeleted(CalendarItem calendarItem)
    {
        base.OnCalendarItemDeleted(calendarItem);

        if (DisplayDetailsCalendarItemViewModel?.Id == calendarItem.Id ||
            DisplayDetailsCalendarItemViewModel?.CalendarItem?.RecurringCalendarItemId == calendarItem.Id)
        {
            DisplayDetailsCalendarItemViewModel = null;
        }

        if (ShouldReloadFor(calendarItem))
        {
            _ = ReloadCurrentVisibleRangeAsync();
        }
    }

    protected override void OnCalendarItemUpdated(CalendarItem calendarItem, CalendarItemUpdateSource source)
    {
        base.OnCalendarItemUpdated(calendarItem, source);

        if (DisplayDetailsCalendarItemViewModel?.Id == calendarItem.Id)
        {
            calendarItem.AssignedCalendar ??= DisplayDetailsCalendarItemViewModel.AssignedCalendar;
            DisplayDetailsCalendarItemViewModel = new CalendarItemViewModel(calendarItem);
        }

        if (ShouldReloadFor(calendarItem))
        {
            _ = ReloadCurrentVisibleRangeAsync();
        }
    }

    protected override void OnCalendarItemAdded(CalendarItem calendarItem)
    {
        base.OnCalendarItemAdded(calendarItem);

        if (calendarItem.IsRecurringParent)
        {
            _ = ReloadCurrentVisibleRangeAsync();
            return;
        }

        if (ShouldReloadFor(calendarItem))
        {
            _ = ReloadCurrentVisibleRangeAsync();
        }
    }

    private bool ShouldReloadFor(CalendarItem calendarItem)
    {
        if (calendarItem == null || LoadedDateWindow == null)
            return false;

        var loadedWindow = new TimeRange(LoadedDateWindow.StartDate, LoadedDateWindow.EndDate);
        return loadedWindow.OverlapsWith(calendarItem.Period);
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
