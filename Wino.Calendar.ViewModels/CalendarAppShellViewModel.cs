using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core.Domain;
using Wino.Core.Domain.Collections;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.Server;
using Wino.Messaging.UI;

namespace Wino.Calendar.ViewModels;

public partial class CalendarAppShellViewModel : CalendarBaseViewModel,
    ICalendarShellClient,
    IRecipient<VisibleDateRangeChangedMessage>,
    IRecipient<CalendarEnableStatusChangedMessage>,
    IRecipient<CalendarDisplayTypeChangedMessage>,
    IRecipient<AccountRemovedMessage>
{
    public IPreferencesService PreferencesService { get; }
    public IStatePersistanceService StatePersistenceService { get; }
    public IAccountCalendarStateService AccountCalendarStateService { get; }
    public INavigationService NavigationService { get; }
    public WinoApplicationMode Mode => WinoApplicationMode.Calendar;
    public bool HandlesNavigationSelection => false;
    System.Collections.IEnumerable ICalendarShellClient.GroupedAccountCalendars => AccountCalendarStateService.GroupedAccountCalendars;
    System.Collections.IEnumerable ICalendarShellClient.DateNavigationHeaderItems => DateNavigationHeaderItems;
    object IShellClient.SelectedMenuItem
    {
        get => null;
        set { }
    }
    System.Windows.Input.ICommand ICalendarShellClient.TodayClickedCommand => TodayClickedCommand;
    System.Windows.Input.ICommand ICalendarShellClient.DateClickedCommand => DateClickedCommand;

    public MenuItemCollection MenuItems { get; private set; }
    public MenuItemCollection FooterItems { get; private set; }

    [ObservableProperty]
    private int _selectedMenuItemIndex = -1;

    [ObservableProperty]
    private bool isCalendarEnabled;



    /// <summary>
    /// Gets or sets the display date of the calendar.
    /// </summary>
    [ObservableProperty]
    private DateTimeOffset _displayDate;

    /// <summary>
    /// Gets or sets the highlighted range in the CalendarView and displayed date range in FlipView.
    /// </summary>
    [ObservableProperty]
    private DateRange highlightedDateRange;

    [ObservableProperty]
    private ObservableRangeCollection<string> dateNavigationHeaderItems = [];

    [ObservableProperty]
    private int _selectedDateNavigationHeaderIndex;

    public bool IsVerticalCalendar => StatePersistenceService.CalendarDisplayType == CalendarDisplayType.Month;

    [ObservableProperty]
    private bool isStoreUpdateItemVisible;

    private readonly SettingsItem _settingsItem = new();
    private readonly StoreUpdateMenuItem _storeUpdateMenuItem = new();

    // For updating account calendars asynchronously.
    private SemaphoreSlim _accountCalendarUpdateSemaphoreSlim = new(1);
    private bool _runtimeSubscriptionsAttached;
    private bool _hasRegisteredPersistentRecipients;

    public CalendarAppShellViewModel(IPreferencesService preferencesService,
                             IStatePersistanceService statePersistanceService,
                             IAccountService accountService,
                             ICalendarService calendarService,
                             IAccountCalendarStateService accountCalendarStateService,
                             INavigationService navigationService,
                             CalendarPageViewModel calendarPageViewModel,
                             IMailDialogService dialogService,
                             IUpdateManager updateManager,
                             IStoreUpdateService storeUpdateService)
    {
        _accountService = accountService;
        _calendarService = calendarService;
        _calendarPageViewModel = calendarPageViewModel;
        _dialogService = dialogService;
        _updateManager = updateManager;
        _storeUpdateService = storeUpdateService;

        AccountCalendarStateService = accountCalendarStateService;

        NavigationService = navigationService;
        PreferencesService = preferencesService;

        StatePersistenceService = statePersistanceService;
    }

    protected override void OnDispatcherAssigned()
    {
        base.OnDispatcherAssigned();

        AccountCalendarStateService.Dispatcher = Dispatcher;
        MenuItems = new MenuItemCollection(Dispatcher);
        FooterItems = new MenuItemCollection(Dispatcher);
        _ = RefreshFooterItemsAsync(false);
    }

    private void PrefefencesChanged(object sender, string e)
    {
        if (e == nameof(StatePersistenceService.CalendarDisplayType))
        {
            Messenger.Send(new CalendarDisplayTypeChangedMessage(StatePersistenceService.CalendarDisplayType));

            UpdateDateNavigationHeaderItems();

            // Change the calendar.
            DateClicked(new CalendarViewDayClickedEventArgs(GetDisplayTypeSwitchDate()));
        }
    }

    private async void PreferencesServiceChanged(object sender, string e)
    {
        if (e == nameof(IPreferencesService.IsStoreUpdateNotificationsEnabled))
        {
            await RefreshFooterItemsAsync(false);
        }
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        if (!_hasRegisteredPersistentRecipients)
        {
            RegisterRecipients();
            _hasRegisteredPersistentRecipients = true;
        }

        AttachRuntimeSubscriptions();

        var activationContext = parameters as ShellModeActivationContext;
        var shouldRunStartupFlows = activationContext?.IsInitialActivation ?? true;

        PreferencesService.PreferenceChanged -= PreferencesServiceChanged;
        PreferencesService.PreferenceChanged += PreferencesServiceChanged;

        await RefreshFooterItemsAsync(mode == NavigationMode.New);

        UpdateDateNavigationHeaderItems();

        await InitializeAccountCalendarsAsync();
        ValidateConfiguredNewEventCalendar();

        if (shouldRunStartupFlows)
        {
            await ShowWhatIsNewIfNeededAsync();
        }

        TodayClicked();
    }

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        DetachRuntimeSubscriptions();
        PreferencesService.PreferenceChanged -= PreferencesServiceChanged;
        _ = ExecuteUIThread(() =>
        {
            DateNavigationHeaderItems.Clear();
            AccountCalendarStateService.ClearGroupedAccountCalendars();
            HighlightedDateRange = null;
            SelectedDateNavigationHeaderIndex = -1;
        });
        _calendarPageViewModel.CleanupForShellDeactivation();
    }

    private void AttachRuntimeSubscriptions()
    {
        if (_runtimeSubscriptionsAttached)
            return;

        AccountCalendarStateService.AccountCalendarSelectionStateChanged += UpdateAccountCalendarRequested;
        AccountCalendarStateService.CollectiveAccountGroupSelectionStateChanged += AccountCalendarStateCollectivelyChanged;
        StatePersistenceService.StatePropertyChanged += PrefefencesChanged;
        _runtimeSubscriptionsAttached = true;
    }

    private void DetachRuntimeSubscriptions()
    {
        if (!_runtimeSubscriptionsAttached)
            return;

        AccountCalendarStateService.AccountCalendarSelectionStateChanged -= UpdateAccountCalendarRequested;
        AccountCalendarStateService.CollectiveAccountGroupSelectionStateChanged -= AccountCalendarStateCollectivelyChanged;
        StatePersistenceService.StatePropertyChanged -= PrefefencesChanged;
        _runtimeSubscriptionsAttached = false;
    }

    private async Task ShowWhatIsNewIfNeededAsync()
    {
        if (!_updateManager.ShouldShowUpdateNotes())
            return;

        var notes = await _updateManager.GetLatestUpdateNotesAsync();

        if (notes.Sections.Count == 0)
            return;

        await _dialogService.ShowWhatIsNewDialogAsync(notes);
    }

    private async Task RefreshFooterItemsAsync(bool showNotification)
    {
        await ExecuteUIThread(() =>
        {
            FooterItems.Clear();
        });
    }

    private async Task StartStoreUpdateAsync()
    {
        await _storeUpdateService.StartUpdateAsync().ConfigureAwait(false);
        await RefreshFooterItemsAsync(false).ConfigureAwait(false);
    }

    private async void AccountCalendarStateCollectivelyChanged(object sender, GroupedAccountCalendarViewModel e)
    {
        // When using three-state checkbox, multiple accounts will be selected/unselected at the same time.
        // Reporting all these changes one by one to the UI is not efficient and may cause problems in the future.

        // Update all calendar states at once.
        try
        {
            await _accountCalendarUpdateSemaphoreSlim.WaitAsync();

            foreach (var calendar in e.AccountCalendars)
            {
                await _calendarService.UpdateAccountCalendarAsync(calendar.AccountCalendar).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while waiting for account calendar update semaphore.");
        }
        finally
        {
            _accountCalendarUpdateSemaphoreSlim.Release();
        }
    }

    private async void UpdateAccountCalendarRequested(object sender, AccountCalendarViewModel e)
        => await _calendarService.UpdateAccountCalendarAsync(e.AccountCalendar).ConfigureAwait(false);

    private async Task InitializeAccountCalendarsAsync()
    {
        await Dispatcher.ExecuteOnUIThread(() => AccountCalendarStateService.ClearGroupedAccountCalendars());

        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);

        foreach (var account in accounts)
        {
            var accountCalendars = await _calendarService.GetAccountCalendarsAsync(account.Id).ConfigureAwait(false);
            var calendarViewModels = new List<AccountCalendarViewModel>();

            foreach (var calendar in accountCalendars)
            {
                var calendarViewModel = new AccountCalendarViewModel(account, calendar);

                calendarViewModels.Add(calendarViewModel);
            }

            var groupedAccountCalendarViewModel = new GroupedAccountCalendarViewModel(account, calendarViewModels);

            await Dispatcher.ExecuteOnUIThread(() =>
            {
                AccountCalendarStateService.AddGroupedAccountCalendar(groupedAccountCalendarViewModel);
            });
        }
    }

    private void ForceNavigateCalendarDate()
    {
        var args = new CalendarPageNavigationArgs()
        {
            NavigationDate = _navigationDate ?? DateTime.Now.Date
        };

        NavigationService.Navigate(WinoPage.CalendarPage, args);
        _navigationDate = null;
    }

    partial void OnSelectedMenuItemIndexChanged(int oldValue, int newValue) { }

    [RelayCommand]
    private async Task Sync()
    {
        // Sync all calendars.
        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);

        foreach (var account in accounts)
        {
            var t = new NewCalendarSynchronizationRequested(new CalendarSynchronizationOptions()
            {
                AccountId = account.Id,
                Type = CalendarSynchronizationType.CalendarEvents
            });

            Messenger.Send(t);
        }
    }

    /// <summary>
    /// When calendar type switches, we need to navigate to the most ideal date.
    /// This method returns that date.
    /// </summary>
    private DateTime GetDisplayTypeSwitchDate()
    {
        var settings = PreferencesService.GetCurrentCalendarSettings();
        switch (StatePersistenceService.CalendarDisplayType)
        {
            case CalendarDisplayType.Day:
                if (HighlightedDateRange.IsInRange(DateTime.Now)) return DateTime.Now.Date;

                return HighlightedDateRange.StartDate;
            case CalendarDisplayType.Week:
                if (HighlightedDateRange == null || HighlightedDateRange.IsInRange(DateTime.Now))
                {
                    return DateTime.Now.Date.GetWeekStartDateForDate(settings.FirstDayOfWeek);
                }

                return HighlightedDateRange.StartDate.GetWeekStartDateForDate(settings.FirstDayOfWeek);
            case CalendarDisplayType.WorkWeek:
                break;
            case CalendarDisplayType.Month:
                break;
            default:
                break;
        }

        return DateTime.Today.Date;
    }

    private DateTime? _navigationDate;
    private readonly IAccountService _accountService;
    private readonly ICalendarService _calendarService;
    private readonly CalendarPageViewModel _calendarPageViewModel;
    private readonly IMailDialogService _dialogService;
    private readonly IUpdateManager _updateManager;
    private readonly IStoreUpdateService _storeUpdateService;

    #region Commands

    [RelayCommand]
    private void TodayClicked()
    {
        _navigationDate = DateTime.Now.Date;

        ForceNavigateCalendarDate();
    }

    public async Task HandleNavigationItemInvokedAsync(IMenuItem menuItem)
    {
        switch (menuItem)
        {
            case NewMailMenuItem:
                await NewEventAsync().ConfigureAwait(false);
                break;
            case SettingsItem:
                NavigationService.Navigate(WinoPage.SettingsPage);
                break;
            case StoreUpdateMenuItem:
                await StartStoreUpdateAsync().ConfigureAwait(false);
                break;
        }
    }

    [RelayCommand]
    private async Task NewEventAsync()
    {
        var pickedCalendar = TryResolveConfiguredNewEventCalendar();

        if (pickedCalendar == null)
        {
            var availableGroups = AccountCalendarStateService.GroupedAccountCalendars
                .Where(group => group.AccountCalendars.Count > 0)
                .Select(group => new CalendarPickerAccountGroup
                {
                    Account = group.Account,
                    Calendars = group.AccountCalendars.Select(calendar => calendar.AccountCalendar).ToList()
                })
                .ToList();

            if (availableGroups.Count == 0)
            {
                _dialogService.InfoBarMessage(
                    Translator.CalendarEventCompose_NoCalendarsTitle,
                    Translator.CalendarEventCompose_NoCalendarsMessage,
                    InfoBarMessageType.Warning);
                return;
            }

            var pickingResult = await _dialogService.ShowSingleCalendarPickerDialogAsync(availableGroups);

            if (pickingResult.ShouldNavigateToCalendarSettings)
            {
                NavigationService.Navigate(WinoPage.CalendarSettingsPage);
                return;
            }

            pickedCalendar = pickingResult.PickedCalendar;
        }

        if (pickedCalendar == null)
            return;

        var (startDate, endDate) = GetDefaultComposeDateRange();

        NavigationService.Navigate(WinoPage.CalendarEventComposePage, new CalendarEventComposeNavigationArgs
        {
            SelectedCalendarId = pickedCalendar.Id,
            StartDate = startDate,
            EndDate = endDate
        });
    }

    public override async Task KeyboardShortcutHook(KeyboardShortcutTriggerDetails args)
    {
        if (args.Handled || args.Mode != WinoApplicationMode.Calendar)
            return;

        if (args.Action == KeyboardShortcutAction.NewEvent)
        {
            await NewEventAsync();
            args.Handled = true;
        }
    }



    [RelayCommand]
    private void DateClicked(CalendarViewDayClickedEventArgs clickedDateArgs)
    {
        _navigationDate = clickedDateArgs.ClickedDate;

        ForceNavigateCalendarDate();
    }

    #endregion

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        UnregisterRecipients();

        Messenger.Register<VisibleDateRangeChangedMessage>(this);
        Messenger.Register<CalendarEnableStatusChangedMessage>(this);
        Messenger.Register<CalendarDisplayTypeChangedMessage>(this);
        Messenger.Register<AccountRemovedMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<VisibleDateRangeChangedMessage>(this);
        Messenger.Unregister<CalendarEnableStatusChangedMessage>(this);
        Messenger.Unregister<CalendarDisplayTypeChangedMessage>(this);
        Messenger.Unregister<AccountRemovedMessage>(this);
    }

    public void Receive(VisibleDateRangeChangedMessage message) => HighlightedDateRange = message.DateRange;

    /// <summary>
    /// Sets the header navigation items based on visible date range and calendar type.
    /// </summary>
    private void UpdateDateNavigationHeaderItems()
    {
        var settings = PreferencesService.GetCurrentCalendarSettings();
        var cultureInfo = settings.CultureInfo ?? CultureInfo.CurrentUICulture;

        var visibleRange = HighlightedDateRange ?? new DateRange(DateTime.Today, DateTime.Today.AddDays(1));
        var headerText = GetHeaderText(visibleRange, cultureInfo);

        DateNavigationHeaderItems.ReplaceRange([headerText]);
        SelectedDateNavigationHeaderIndex = DateNavigationHeaderItems.Count > 0 ? 0 : -1;
    }

    private string GetHeaderText(DateRange visibleRange, CultureInfo cultureInfo)
    {
        var startDate = visibleRange.StartDate.Date;
        var endDate = visibleRange.EndDate.Date > startDate ? visibleRange.EndDate.Date.AddDays(-1) : startDate;

        switch (StatePersistenceService.CalendarDisplayType)
        {
            case CalendarDisplayType.Day:
                return startDate.ToString("MMMM d, dddd", cultureInfo);
            case CalendarDisplayType.Week:
            case CalendarDisplayType.WorkWeek:
                if (startDate.Month == endDate.Month && startDate.Year == endDate.Year)
                {
                    return $"{startDate.ToString("MMMM d", cultureInfo)} - {endDate.ToString("%d", cultureInfo)}";
                }

                return $"{startDate.ToString("MMMM d", cultureInfo)} - {endDate.ToString("MMMM d", cultureInfo)}";
            case CalendarDisplayType.Month:
                return GetDominantMonthHeaderText(startDate, endDate, cultureInfo);
            default:
                return startDate.ToString("d", cultureInfo);
        }
    }

    private static string GetDominantMonthHeaderText(DateTime startDate, DateTime endDate, CultureInfo cultureInfo)
    {
        if (endDate < startDate)
        {
            endDate = startDate;
        }

        var monthDayCounts = new Dictionary<(int Year, int Month), int>();

        for (var day = startDate; day <= endDate; day = day.AddDays(1))
        {
            var key = (day.Year, day.Month);

            if (monthDayCounts.TryGetValue(key, out var count))
            {
                monthDayCounts[key] = count + 1;
            }
            else
            {
                monthDayCounts[key] = 1;
            }
        }

        var dominantKey = (Year: startDate.Year, Month: startDate.Month);
        var dominantCount = -1;

        foreach (var pair in monthDayCounts)
        {
            if (pair.Value > dominantCount)
            {
                dominantCount = pair.Value;
                dominantKey = pair.Key;
            }
        }

        return new DateTime(dominantKey.Year, dominantKey.Month, 1).ToString("Y", cultureInfo);
    }

    partial void OnHighlightedDateRangeChanged(DateRange value)
    {
        UpdateDateNavigationHeaderItems();
    }

    public async void Receive(CalendarEnableStatusChangedMessage message)
        => await ExecuteUIThread(() => IsCalendarEnabled = message.IsEnabled);

    public void Receive(CalendarDisplayTypeChangedMessage message)
    {
        OnPropertyChanged(nameof(IsVerticalCalendar));
        UpdateDateNavigationHeaderItems();
    }

    public async void Receive(AccountRemovedMessage message)
    {
        await InitializeAccountCalendarsAsync();
        ValidateConfiguredNewEventCalendar();
    }

    private AccountCalendar TryResolveConfiguredNewEventCalendar()
    {
        ValidateConfiguredNewEventCalendar();

        if (PreferencesService.NewEventButtonBehavior != NewEventButtonBehavior.AlwaysUseSpecificCalendar
            || !PreferencesService.DefaultNewEventCalendarId.HasValue)
        {
            return null;
        }

        return AccountCalendarStateService.AllCalendars
            .FirstOrDefault(calendar => calendar.Id == PreferencesService.DefaultNewEventCalendarId.Value)?
            .AccountCalendar;
    }

    private void ValidateConfiguredNewEventCalendar()
    {
        if (PreferencesService.NewEventButtonBehavior != NewEventButtonBehavior.AlwaysUseSpecificCalendar
            || !PreferencesService.DefaultNewEventCalendarId.HasValue)
        {
            return;
        }

        var exists = AccountCalendarStateService.AllCalendars
            .Any(calendar => calendar.Id == PreferencesService.DefaultNewEventCalendarId.Value);

        if (exists)
            return;

        PreferencesService.NewEventButtonBehavior = NewEventButtonBehavior.AskEachTime;
        PreferencesService.DefaultNewEventCalendarId = null;
    }

    private static (DateTime StartDate, DateTime EndDate) GetDefaultComposeDateRange()
    {
        var localNow = DateTime.Now;
        var roundedMinutes = localNow.Minute switch
        {
            < 30 => 30,
            30 when localNow.Second == 0 && localNow.Millisecond == 0 => 30,
            _ => 60
        };

        var startDate = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, 0, 0);
        startDate = roundedMinutes == 60 ? startDate.AddHours(1) : startDate.AddMinutes(roundedMinutes);

        return (startDate, startDate.AddMinutes(30));
    }

    void IShellClient.Activate(ShellModeActivationContext activationContext)
        => OnNavigatedTo(NavigationMode.New, activationContext);

    void IShellClient.Deactivate()
        => OnNavigatedFrom(NavigationMode.New, null!);

    Task IShellClient.HandleNavigationItemInvokedAsync(IMenuItem menuItem)
        => menuItem == null ? Task.CompletedTask : HandleNavigationItemInvokedAsync(menuItem);

    Task IShellClient.HandleNavigationSelectionChangedAsync(IMenuItem menuItem)
        => Task.CompletedTask;
}









