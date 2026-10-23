using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Itenso.TimePeriod;
using Serilog;
using MailCal.Calendar.ViewModels.Data;
using MailCal.Calendar.ViewModels.Interfaces;
using MailCal.Calendar.ViewModels.Messages;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Collections;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Extensions;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Core.Domain.Models.Navigation;
using MailCal.Core.Services;
using MailCal.Core.ViewModels;
using MailCal.Messaging.Client.Calendar;
using MailCal.Messaging.Client.Shell;
using MailCal.Messaging.UI;

namespace MailCal.Calendar.ViewModels;

public partial class CalendarPageViewModel : CalendarBaseViewModel,
    IRecipient<LoadCalendarMessage>,
    IRecipient<CalendarItemDeleted>,
    IRecipient<PreferenceChangedMessage>,
    IRecipient<CalendarItemTappedMessage>,
    IRecipient<CalendarItemDoubleTappedMessage>,
    IRecipient<CalendarItemRightTappedMessage>,
    IRecipient<CalendarItemContextActionRequestedMessage>,
    IRecipient<AccountRemovedMessage>,
    IShellMenuOwner,
    IDisposable
{
    #region Quick Event Creation

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedQuickEventAccountCalendarName))]
    [NotifyPropertyChangedFor(nameof(CanSaveQuickEvent))]
    [NotifyCanExecuteChangedFor(nameof(SaveQuickEventCommand))]
    public partial AccountCalendarViewModel SelectedQuickEventAccountCalendar { get; set; }

    public string SelectedQuickEventAccountCalendarName
        => SelectedQuickEventAccountCalendar == null ? "Pick a calendar" : SelectedQuickEventAccountCalendar.Name;

    [ObservableProperty]
    public partial List<string> HourSelectionStrings { get; set; } = [];

    private string _previousSelectedStartTimeString = string.Empty;
    private string _previousSelectedEndTimeString = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveQuickEvent))]
    [NotifyCanExecuteChangedFor(nameof(SaveQuickEventCommand))]
    [NotifyPropertyChangedFor(nameof(QuickEventDateRangeText))]
    [NotifyPropertyChangedFor(nameof(QuickEventSelectionEnd))]
    public partial DateTime? SelectedQuickEventDate { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveQuickEvent))]
    [NotifyCanExecuteChangedFor(nameof(SaveQuickEventCommand))]
    [NotifyPropertyChangedFor(nameof(QuickEventDateRangeText))]
    [NotifyPropertyChangedFor(nameof(QuickEventSelectionEnd))]
    public partial bool IsAllDay { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveQuickEvent))]
    [NotifyCanExecuteChangedFor(nameof(SaveQuickEventCommand))]
    public partial string SelectedStartTimeString { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveQuickEvent))]
    [NotifyCanExecuteChangedFor(nameof(SaveQuickEventCommand))]
    [NotifyPropertyChangedFor(nameof(QuickEventDateRangeText))]
    [NotifyPropertyChangedFor(nameof(QuickEventSelectionEnd))]
    public partial string SelectedEndTimeString { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Location { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveQuickEvent))]
    [NotifyCanExecuteChangedFor(nameof(SaveQuickEventCommand))]
    public partial string EventName { get; set; } = string.Empty;

    public DateTime QuickEventStartTime => SelectedQuickEventDate.Value.Date.Add(CurrentSettings.GetTimeSpan(SelectedStartTimeString).Value);
    public DateTime QuickEventEndTime => (SelectedQuickEventEndDate ?? SelectedQuickEventDate.Value).Date.Add(CurrentSettings.GetTimeSpan(SelectedEndTimeString).Value);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveQuickEvent))]
    [NotifyPropertyChangedFor(nameof(QuickEventDateRangeText))]
    [NotifyPropertyChangedFor(nameof(QuickEventSelectionEnd))]
    [NotifyCanExecuteChangedFor(nameof(SaveQuickEventCommand))]
    public partial DateTime? SelectedQuickEventEndDate { get; set; }

    public DateTime? QuickEventSelectionEnd => SelectedQuickEventDate == null ? null
        : IsAllDay ? GetQuickEventAllDayEnd()
        : CurrentSettings.GetTimeSpan(SelectedEndTimeString) is TimeSpan endTime
            ? (SelectedQuickEventEndDate ?? SelectedQuickEventDate.Value).Date.Add(endTime)
            : null;

    public string QuickEventDateRangeText
    {
        get
        {
            if (SelectedQuickEventDate is not DateTime start)
                return string.Empty;

            var end = IsAllDay ? GetQuickEventAllDayEnd().AddDays(-1) : SelectedQuickEventEndDate ?? start;
            var culture = CurrentSettings.CultureInfo;
            return start.Date == end.Date ? start.ToString("D", culture)
                : $"{start.ToString("d", culture)} – {end.ToString("d", culture)}";
        }
    }

    private DateTime GetQuickEventAllDayEnd()
    {
        var start = SelectedQuickEventDate.Value.Date;
        return SelectedQuickEventEndDate is DateTime end && end.Date > start ? end.Date : start.AddDays(1);
    }

    partial void OnSelectedQuickEventDateChanged(DateTime? value)
    {
        if (value == null)
            SelectedQuickEventEndDate = null;
    }

    public void SelectQuickEventRange(DateTime start, DateTime end, bool isAllDay)
    {
        if (end <= start)
            throw new ArgumentOutOfRangeException(nameof(end));

        SelectedQuickEventDate = start;
        SelectedQuickEventEndDate = end.Date;
        IsAllDay = isAllDay;
        SelectQuickEventTimeRange(start.TimeOfDay, end.TimeOfDay);
    }

    public bool CanSaveQuickEvent
    {
        get
        {
            if (!Readiness.IsReady ||
                SelectedQuickEventAccountCalendar == null ||
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

            return IsAllDay || QuickEventEndTime > QuickEventStartTime;
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

    /// <summary>
    /// Every item inside <see cref="LoadedDateWindow"/>, sorted by start, end and id. Window moves
    /// add and remove whole ranges; single edits insert, replace or remove one item.
    /// </summary>
    [ObservableProperty]
    public partial ObservableRangeCollection<CalendarItemViewModel> CalendarItems { get; set; } = new();

    #endregion

    #region Event Details

    public event EventHandler DetailsShowCalendarItemChanged;

    public bool CanJoinOnline => CalendarJoinLinkResolver.TryGetEffectiveJoinUri(
        DisplayDetailsCalendarItemViewModel?.CalendarItem,
        out _);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEventDetailsVisible))]
    [NotifyCanExecuteChangedFor(nameof(JoinOnlineCommand))]
    [NotifyPropertyChangedFor(nameof(CanJoinOnline))]
    public partial CalendarItemViewModel DisplayDetailsCalendarItemViewModel { get; set; }

    public bool IsEventDetailsVisible => DisplayDetailsCalendarItemViewModel != null;

    #endregion

    private readonly ICalendarService _calendarService;
    private readonly INavigationService _navigationService;
    private readonly IExternalLauncher _externalLauncher;
    private readonly INotificationBuilder _notificationBuilder;
    private readonly IPreferencesService _preferencesService;
    private readonly IMailCalRequestDelegator _mailCalRequestDelegator;
    private readonly IMailDialogService _dialogService;
    private readonly IDateContextProvider _dateContextProvider;
    private readonly ICalendarRangeTextFormatter _calendarRangeTextFormatter;
    private readonly CoalescedRefresh _calendarSettingsRefresh;

    private readonly SemaphoreSlim _calendarLoadingSemaphore = new(1);
    private bool _subscriptionsAttached;
    private CancellationTokenSource _pageLifetimeCts = new();
    private long _pageLifetimeVersion;
    private long _displayRequestVersion;
    private bool _isCalendarBadgeClearedForPageLifetime;
    private Dictionary<Guid, CalendarItemViewModel> _loadedCalendarItems = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveQuickEvent))]
    public partial CalendarSettings CurrentSettings { get; set; }

    public IStatePersistanceService StatePersistanceService { get; }
    public IAccountCalendarStateService AccountCalendarStateService { get; }

    /// <summary>
    /// Blocked state while Calendar cannot be used: no account, Calendar turned off everywhere,
    /// a sign-in pending, or the first sync still running. Event creation waits for it.
    /// </summary>
    public MailCal.Core.ViewModels.Data.ModeReadinessViewModel Readiness { get; }

    /// <summary>Mirrors <see cref="Readiness"/> for the shell, which follows this view model's property changes.</summary>
    public bool IsCalendarModeReady => Readiness.IsReady;

    private void ReadinessChanged(object sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsCalendarModeReady));
        OnPropertyChanged(nameof(CanSaveQuickEvent));
        SaveQuickEventCommand.NotifyCanExecuteChanged();

        if (!Readiness.IsReady)
        {
            // Drop a half-made quick event; there is nowhere to save it any more.
            SelectedQuickEventDate = null;
            return;
        }

        EnsureSelectedQuickEventAccountCalendar();
        _ = ReloadCurrentVisibleRangeAsync();
    }

    /// <summary>
    /// The calendar pane belongs to the calendar mode view model. The page hands it to the
    /// shell when the inner frame navigates here.
    /// </summary>
    public IShellMenuProvider ShellMenuProvider { get; }

    public CalendarPageViewModel(
        IStatePersistanceService statePersistanceService,
        ICalendarService calendarService,
        INavigationService navigationService,
        IExternalLauncher externalLauncher,
        IAccountCalendarStateService accountCalendarStateService,
        INotificationBuilder notificationBuilder,
        IPreferencesService preferencesService,
        IMailCalRequestDelegator mailCalRequestDelegator,
        IMailDialogService dialogService,
        IDateContextProvider dateContextProvider,
        ICalendarRangeTextFormatter calendarRangeTextFormatter,
        ICalendarShellClient shellMenuProvider,
        IAppModeReadinessService appModeReadinessService = null,
        IMailShellClient mailShell = null)
    {
        Readiness = new MailCal.Core.ViewModels.Data.ModeReadinessViewModel(
            MailCalApplicationMode.Calendar,
            appModeReadinessService,
            navigationService,
            mailShell,
            ExecuteUIThread);
        Readiness.ReadinessChanged += ReadinessChanged;
        ShellMenuProvider = shellMenuProvider;
        StatePersistanceService = statePersistanceService;
        AccountCalendarStateService = accountCalendarStateService;
        _calendarService = calendarService;
        _navigationService = navigationService;
        _externalLauncher = externalLauncher;
        _notificationBuilder = notificationBuilder;
        _preferencesService = preferencesService;
        _mailCalRequestDelegator = mailCalRequestDelegator;
        _dialogService = dialogService;
        _dateContextProvider = dateContextProvider;
        _calendarRangeTextFormatter = calendarRangeTextFormatter;
        _calendarSettingsRefresh = new CoalescedRefresh(ApplyCalendarSettingsChangeAsync);

        RefreshSettings();
    }

    public override async Task KeyboardShortcutHook(KeyboardShortcutTriggerDetails args)
    {
        if (args.Handled || args.Mode != MailCalApplicationMode.Calendar || args.Action != KeyboardShortcutAction.Delete)
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

        await _mailCalRequestDelegator.ExecuteAsync(preparationRequest);
        DisplayDetailsCalendarItemViewModel = null;
        args.Handled = true;
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        Messenger.Unregister<LoadCalendarMessage>(this);
        Messenger.Unregister<PreferenceChangedMessage>(this);
        Messenger.Unregister<CalendarItemTappedMessage>(this);
        Messenger.Unregister<CalendarItemDoubleTappedMessage>(this);
        Messenger.Unregister<CalendarItemRightTappedMessage>(this);
        Messenger.Unregister<CalendarItemContextActionRequestedMessage>(this);
        Messenger.Unregister<AccountRemovedMessage>(this);

        Messenger.Register<LoadCalendarMessage>(this);
        Messenger.Register<PreferenceChangedMessage>(this);
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
        Messenger.Unregister<PreferenceChangedMessage>(this);
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
        if (!CalendarJoinLinkResolver.TryGetEffectiveJoinUri(DisplayDetailsCalendarItemViewModel?.CalendarItem, out var joinUri))
            return;

        (await _externalLauncher.LaunchUriAsync(joinUri)).ThrowIfNotSucceeded();
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        ResetPageLifetime();
        base.OnNavigatedTo(mode, parameters);
        AttachSubscriptions();
        RefreshSettings();
        IsCalendarEnabled = true;
        EnsureSelectedQuickEventAccountCalendar();
        _ = Readiness.ActivateAsync();
    }

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        base.OnNavigatedFrom(mode, parameters);
        Readiness.Deactivate();

        if (StatePersistanceService.ApplicationMode == MailCalApplicationMode.Calendar)
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
        _navigationService.Navigate(MailCalPage.EventDetailsPage, target);
    }

    [RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanSaveQuickEvent))]
    private async Task SaveQuickEventAsync()
    {
        LastQuickCreationSucceeded = false;
        if (SelectedQuickEventAccountCalendar?.IsReadOnly == true)
        {
            _dialogService.ShowReadOnlyCalendarMessage();
            return;
        }

        var startDate = IsAllDay ? SelectedQuickEventDate.Value.Date : QuickEventStartTime;
        var endDate = IsAllDay ? GetQuickEventAllDayEnd() : QuickEventEndTime;
        var composeResult = QuickEventFactory.Create(
            SelectedQuickEventAccountCalendar, EventName, Location, startDate, endDate, IsAllDay);

        await QuickEventFactory.CreateAsync(_mailCalRequestDelegator, composeResult);
        LastQuickCreationSucceeded = true;
    }

    public bool LastQuickCreationSucceeded { get; private set; }

    [RelayCommand]
    private void GoToEventComposePage()
    {
        if (SelectedQuickEventDate == null || !Readiness.IsReady)
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
                endDate = (SelectedQuickEventEndDate ?? SelectedQuickEventDate.Value).Date.Add(selectedEndTime.Value);
            }
        }
        else
        {
            startDate = SelectedQuickEventDate.Value.Date;
            endDate = GetQuickEventAllDayEnd();
        }

        _navigationService.Navigate(MailCalPage.CalendarEventComposePage, new CalendarEventComposeNavigationArgs
        {
            HasPendingInput = true,
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
        if (!CanChangeCalendarItemTimes(calendarItemViewModel))
        {
            return;
        }

        var calendarItem = calendarItemViewModel.CalendarItem;
        var normalizedTargetStart = calendarItem.IsAllDayEvent
            ? targetStart.Date
            : targetStart;

        await ChangeCalendarItemTimesAsync(calendarItemViewModel, normalizedTargetStart, calendarItem.DurationInSeconds).ConfigureAwait(false);
    }

    /// <summary>
    /// Changes a timed event's end while keeping its start (bottom-edge resize on macOS).
    /// Uses the same validation and ChangeStartAndEndDate persistence path as <see cref="MoveCalendarItemAsync"/>.
    /// </summary>
    /// <param name="calendarItemViewModel">Event to resize.</param>
    /// <param name="targetEnd">New end in local time. Must be after the event start.</param>
    public async Task ResizeCalendarItemAsync(CalendarItemViewModel calendarItemViewModel, DateTime targetEnd)
    {
        if (!CanChangeCalendarItemTimes(calendarItemViewModel))
        {
            return;
        }

        var calendarItem = calendarItemViewModel.CalendarItem;

        if (calendarItem.IsAllDayEvent)
        {
            return;
        }

        var localStart = calendarItem.LocalStartDate;

        if (targetEnd <= localStart)
        {
            return;
        }

        await ChangeCalendarItemTimesAsync(calendarItemViewModel, localStart, (targetEnd - localStart).TotalSeconds).ConfigureAwait(false);
    }

    /// <summary>Shows the Windows refusal info bars for events whose times cannot change.</summary>
    private bool CanChangeCalendarItemTimes(CalendarItemViewModel calendarItemViewModel)
    {
        var calendarItem = calendarItemViewModel?.CalendarItem;

        if (calendarItem == null)
        {
            return false;
        }

        if (!calendarItem.CanChangeStartAndEndDate)
        {
            _dialogService.InfoBarMessage(
                Translator.CalendarDragDropMoveNotAllowedTitle,
                Translator.CalendarDragDropMoveNotAllowedMessage,
                InfoBarMessageType.Warning);
            return false;
        }

        if (calendarItem.AssignedCalendar?.IsReadOnly == true)
        {
            _dialogService.ShowReadOnlyCalendarMessage();
            return false;
        }

        return true;
    }

    private async Task ChangeCalendarItemTimesAsync(CalendarItemViewModel calendarItemViewModel, DateTime localStart, double durationInSeconds)
    {
        var calendarItem = calendarItemViewModel.CalendarItem;
        var targetEnd = localStart.AddSeconds(durationInSeconds);
        var currentLocalStart = calendarItem.LocalStartDate;
        var currentLocalEnd = calendarItem.LocalEndDate;

        if (currentLocalStart == localStart && currentLocalEnd == targetEnd)
        {
            return;
        }

        var originalItem = CloneCalendarItem(calendarItem);
        var attendees = await _calendarService.GetAttendeesAsync(calendarItem.EventTrackingId).ConfigureAwait(false) ?? [];
        var originalAttendees = CloneAttendees(attendees);

        await ExecuteUIThread(() =>
        {
            calendarItemViewModel.StartDate = localStart;
            calendarItemViewModel.DurationInSeconds = durationInSeconds;
        }).ConfigureAwait(false);

        await _calendarService.UpdateCalendarItemAsync(calendarItem, attendees).ConfigureAwait(false);

        var preparationRequest = new CalendarOperationPreparationRequest(
            CalendarSynchronizerOperation.ChangeStartAndEndDate,
            calendarItem,
            attendees,
            ResponseMessage: null,
            OriginalItem: originalItem,
            OriginalAttendees: originalAttendees);

        await _mailCalRequestDelegator.ExecuteAsync(preparationRequest).ConfigureAwait(false);
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
                timeStrings.Add(CurrentSettings.GetTimeString(new TimeSpan(hour, minute, 0)));
            }
        }

        HourSelectionStrings = timeStrings;

        if (CurrentVisibleRange != null)
        {
            VisibleDateRangeText = _calendarRangeTextFormatter.Format(CurrentVisibleRange, _dateContextProvider);
        }
    }

    public bool LastDisplayLoadSucceeded { get; private set; }

    /// <summary>
    /// The title for a period the surface is passing while its rail moves, before that period is
    /// settled and loaded. Same formatter as <see cref="VisibleDateRangeText"/>.
    /// </summary>
    public string FormatRangeText(CalendarDisplayType displayType, DateOnly anchorDate)
    {
        if (CurrentSettings == null)
            return string.Empty;

        var today = _dateContextProvider.GetToday();
        var range = CalendarRangeResolver.Resolve(new CalendarDisplayRequest(displayType, anchorDate), CurrentSettings, today);

        return _calendarRangeTextFormatter.Format(range, _dateContextProvider);
    }

    public async Task ApplyDisplayRequestAsync(CalendarDisplayRequest request, bool forceReload = false, CalendarItemTarget pendingTarget = null)
    {
        var lifetimeVersion = CurrentPageLifetimeVersion;
        var requestVersion = Interlocked.Increment(ref _displayRequestVersion);
        var hasLoadingLock = await WaitForCalendarLoadingLockAsync(lifetimeVersion).ConfigureAwait(false);
        var loadSucceeded = false;

        if (!hasLoadingLock)
            return;

        try
        {
            // Railing through several periods queues a request per settle; only the latest one is worth loading.
            if (pendingTarget == null && !forceReload && requestVersion != Interlocked.Read(ref _displayRequestVersion))
                return;

            if (!IsPageActive(lifetimeVersion))
                return;

            CalendarSettings currentSettings = null;
            VisibleDateRange currentVisibleRange = null;
            DateRange currentLoadedDateWindow = null;
            HashSet<Guid> loadedItemIds = null;

            await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () =>
            {
                if (CurrentSettings == null)
                {
                    RefreshSettings();
                }

                currentSettings = CurrentSettings;
                currentVisibleRange = CurrentVisibleRange;
                currentLoadedDateWindow = LoadedDateWindow;
                loadedItemIds = _loadedCalendarItems.Keys.ToHashSet();
                LastDisplayLoadSucceeded = false;

                // The surface stays interactive while a window loads; only the first load has nothing to show yet.
                if (currentVisibleRange == null)
                {
                    IsCalendarEnabled = false;
                }
            }).ConfigureAwait(false);

            if (currentSettings == null || !IsPageActive(lifetimeVersion))
                return;

            var today = _dateContextProvider.GetToday();
            var visibleRange = CalendarRangeResolver.Resolve(request, currentSettings, today);
            var loadedDateWindow = CalendarLoadWindow.GetWindow(visibleRange, currentSettings, today);
            var missingRanges = forceReload
                ? new[] { loadedDateWindow }
                : CalendarLoadWindow.GetMissingRanges(currentLoadedDateWindow, loadedDateWindow);

            // The range is published before its items arrive so the title and the surface follow the rail at once.
            await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () =>
            {
                EnsureSelectedQuickEventAccountCalendar();
                CurrentVisibleRange = visibleRange;
                VisibleDateRangeText = _calendarRangeTextFormatter.Format(visibleRange, _dateContextProvider);
            }).ConfigureAwait(false);

            var loadedItems = new List<CalendarItemViewModel>();

            foreach (var missingRange in missingRanges)
            {
                loadedItems.AddRange(await LoadCalendarItemsAsync(missingRange, lifetimeVersion).ConfigureAwait(false));

                if (!IsPageActive(lifetimeVersion))
                    return;
            }

            // The diff runs off the UI thread; the UI thread only applies the sorted result.
            var addedItems = forceReload
                ? loadedItems
                : loadedItems
                    .Where(item => !loadedItemIds.Contains(item.Id))
                    .DistinctBy(item => item.Id)
                    .OrderBy(item => item, Comparer<CalendarItemViewModel>.Create(CompareCalendarItems))
                    .ToList();

            await ExecuteUIThreadIfActiveAsync(lifetimeVersion, () =>
            {
                if (forceReload)
                {
                    ReplaceLoadedCalendarItems(addedItems);
                }
                else
                {
                    ApplyLoadedWindowChanges(loadedDateWindow, addedItems);
                }

                LoadedDateWindow = loadedDateWindow;
                LastDisplayLoadSucceeded = true;
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

    public async Task ReloadCurrentVisibleRangeAsync()
    {
        VisibleDateRange visibleRange = null;

        await ExecuteUIThread(() =>
        {
            visibleRange = CurrentVisibleRange;
            if (visibleRange != null)
            {
                RefreshSettings();
            }
        }).ConfigureAwait(false);

        if (visibleRange == null)
            return;

        await ApplyDisplayRequestAsync(
            new CalendarDisplayRequest(visibleRange.DisplayType, visibleRange.AnchorDate),
            forceReload: true).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CalendarItem>> SearchCalendarItemsAsync(string queryText, int limit, CancellationToken cancellationToken)
    {
        var results = await _calendarService.SearchCalendarItemsAsync(queryText, limit, cancellationToken).ConfigureAwait(false);
        HashSet<Guid> activeCalendarIds = null;

        await ExecuteUIThread(() =>
        {
            activeCalendarIds = AccountCalendarStateService.ActiveCalendars
                .Select(calendar => calendar.Id)
                .ToHashSet();
        }).ConfigureAwait(false);

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
        AccountCalendarViewModel assignedCalendar = null;

        await ExecuteUIThread(() =>
        {
            _loadedCalendarItems.TryGetValue(target.Item.Id, out calendarItemViewModel);
        }).ConfigureAwait(false);

        if (calendarItemViewModel == null)
        {
            var targetItem = await _calendarService.GetCalendarItemTargetAsync(target).ConfigureAwait(false);
            if (targetItem == null)
                return;

            await ExecuteUIThread(() =>
            {
                assignedCalendar = AccountCalendarStateService.ActiveCalendars
                    .FirstOrDefault(calendar => calendar.Id == targetItem.CalendarId);
            }).ConfigureAwait(false);

            targetItem.AssignedCalendar ??= assignedCalendar;
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
        List<AccountCalendarViewModel> activeCalendars = null;

        await ExecuteUIThread(() =>
        {
            activeCalendars = AccountCalendarStateService.ActiveCalendars.ToList();
        }).ConfigureAwait(false);

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

    public void Receive(PreferenceChangedMessage message)
    {
        if (CalendarSettings.PreferenceKeys.Contains(message.PropertyName))
        {
            _calendarSettingsRefresh.Request();
        }
    }

    private async Task ApplyCalendarSettingsChangeAsync()
    {
        await ExecuteUIThread(RefreshSettings);
        await ReloadCurrentVisibleRangeAsync().ConfigureAwait(false);
    }

    public async void Receive(CalendarItemTappedMessage message)
    {
        if (message.CalendarItemViewModel == null)
            return;

        await ExecuteUIThread(() =>
            DisplayDetailsCalendarItemViewModel = message.CalendarItemViewModel);
    }

    public async void Receive(CalendarItemDoubleTappedMessage message)
        => await ExecuteUIThread(() =>
            NavigateEvent(message.CalendarItemViewModel, CalendarEventTargetType.Single));

    public async void Receive(CalendarItemRightTappedMessage message)
    {
        if (message.CalendarItemViewModel == null)
            return;

        await ExecuteUIThread(() =>
            DisplayDetailsCalendarItemViewModel = message.CalendarItemViewModel);
    }

    public async void Receive(CalendarItemContextActionRequestedMessage message)
    {
        if (message.CalendarItemViewModel == null)
            return;

        if (message.Action.ActionType == CalendarContextMenuActionType.Open)
        {
            await ExecuteUIThread(() =>
                NavigateEvent(
                    message.CalendarItemViewModel,
                    message.Action.TargetType ?? CalendarEventTargetType.Single));
            return;
        }

        _ = ExecuteContextActionAsync(message.CalendarItemViewModel, message.Action);
    }

    public async void Receive(AccountRemovedMessage message)
    {
        await ExecuteUIThread(() =>
        {
            if (DisplayDetailsCalendarItemViewModel?.AssignedCalendar?.AccountId == message.Account.Id)
            {
                DisplayDetailsCalendarItemViewModel = null;
            }

            EnsureSelectedQuickEventAccountCalendar();
        });
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

    protected override void OnCalendarItemOperationsCompleted(Guid accountId, IReadOnlyCollection<Guid> calendarItemIds)
    {
        base.OnCalendarItemOperationsCompleted(accountId, calendarItemIds);

        if (calendarItemIds == null || calendarItemIds.Count == 0)
            return;

        var completedItemIds = calendarItemIds.ToHashSet();

        foreach (var calendarItemViewModel in CalendarItems)
        {
            if (calendarItemViewModel.IsBusy && HasPendingCalendarOperation(calendarItemViewModel.CalendarItem, completedItemIds))
            {
                calendarItemViewModel.IsBusy = false;
            }
        }

        if (DisplayDetailsCalendarItemViewModel?.IsBusy == true &&
            HasPendingCalendarOperation(DisplayDetailsCalendarItemViewModel.CalendarItem, completedItemIds))
        {
            DisplayDetailsCalendarItemViewModel.IsBusy = false;
        }
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

            IMailCalSynchronizerBase synchronizer;
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
            DirectJoinLink = calendarItem.DirectJoinLink,
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
        CalendarItems.ReplaceRange(loadedItemsList);
        _loadedCalendarItems = loadedItemsList.ToDictionary(item => item.Id);
    }

    /// <summary>
    /// Moves the loaded window: items that no longer touch it leave in one range removal, and the
    /// newly loaded items join in one range insertion at their sorted position. A window that only
    /// shifted by a few periods inserts at one end; anything else merges and resets once.
    /// </summary>
    private void ApplyLoadedWindowChanges(DateRange loadedDateWindow, List<CalendarItemViewModel> addedItems)
    {
        var evictedItems = _loadedCalendarItems.Values
            .Where(item => !CalendarLoadWindow.Intersects(loadedDateWindow, item.StartDate, item.EndDate))
            .ToList();

        foreach (var evictedItem in evictedItems)
        {
            _loadedCalendarItems.Remove(evictedItem.Id);
        }

        if (evictedItems.Count > 0)
        {
            CalendarItems.RemoveRange(evictedItems, NotifyCollectionChangedAction.Remove);
        }

        var insertedItems = new List<CalendarItemViewModel>(addedItems.Count);

        foreach (var addedItem in addedItems)
        {
            if (_loadedCalendarItems.ContainsKey(addedItem.Id))
                continue;

            _loadedCalendarItems[addedItem.Id] = addedItem;
            insertedItems.Add(addedItem);
        }

        if (insertedItems.Count == 0)
            return;

        if (CalendarItems.Count == 0 || CompareCalendarItems(insertedItems[0], CalendarItems[^1]) >= 0)
        {
            CalendarItems.AddRange(insertedItems);
        }
        else if (CompareCalendarItems(insertedItems[^1], CalendarItems[0]) <= 0)
        {
            CalendarItems.InsertRange(0, insertedItems);
        }
        else
        {
            var mergedItems = CalendarItems
                .Concat(insertedItems)
                .OrderBy(item => item, Comparer<CalendarItemViewModel>.Create(CompareCalendarItems))
                .ToList();

            CalendarItems.ReplaceRange(mergedItems);
        }
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

    public async Task ExecuteCalendarContextActionAsync(CalendarItemViewModel item, CalendarContextMenuAction action)
    {
        if (item?.CalendarItem == null || item.IsBusy) return;
        if (action.ActionType == CalendarContextMenuActionType.Open)
        {
            await ExecuteUIThread(() => NavigateEvent(item, action.TargetType ?? CalendarEventTargetType.Single));
            return;
        }
        await ExecuteContextActionAsync(item, action);
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
        if (!CalendarJoinLinkResolver.TryGetEffectiveJoinUri(calendarItemViewModel?.CalendarItem, out var joinUri))
            return Task.CompletedTask;

        return _externalLauncher.LaunchUriAsync(joinUri);
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
            var confirmed = await ExecuteUIThreadAsync(() =>
                _dialogService.ShowConfirmationDialogAsync(
                    Translator.DialogMessage_DeleteRecurringSeriesMessage,
                    Translator.DialogMessage_DeleteRecurringSeriesTitle,
                    Translator.Buttons_Delete)).ConfigureAwait(false);

            if (!confirmed)
                return;
        }

        var preparationRequest = new CalendarOperationPreparationRequest(
            CalendarSynchronizerOperation.DeleteEvent,
            targetItem,
            null);

        await _mailCalRequestDelegator.ExecuteAsync(preparationRequest).ConfigureAwait(false);
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

        await _mailCalRequestDelegator.ExecuteAsync(preparationRequest).ConfigureAwait(false);
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

        await _mailCalRequestDelegator.ExecuteAsync(preparationRequest).ConfigureAwait(false);
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
            AccountCalendarViewModel assignedCalendar = null;
            targetItem.AssignedCalendar = await _calendarService.GetAccountCalendarAsync(targetItem.CalendarId).ConfigureAwait(false)
                                        ?? calendarItemViewModel.AssignedCalendar;

            if (targetItem.AssignedCalendar == null)
            {
                await ExecuteUIThread(() =>
                {
                    assignedCalendar = AccountCalendarStateService.AllCalendars
                        .FirstOrDefault(calendar => calendar.Id == targetItem.CalendarId);
                }).ConfigureAwait(false);

                targetItem.AssignedCalendar = assignedCalendar;
            }
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
