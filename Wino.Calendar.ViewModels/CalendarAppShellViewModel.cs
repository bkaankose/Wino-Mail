using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core.Domain;
using Wino.Core.Domain.Collections;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.Server;
using Wino.Messaging.UI;

namespace Wino.Calendar.ViewModels;

public partial class CalendarAppShellViewModel : CalendarBaseViewModel,
    ICalendarShellClient,
    IRecipient<CalendarDisplayTypeChangedMessage>,
    IRecipient<AccountRemovedMessage>
{
    public IPreferencesService PreferencesService { get; }
    public IStatePersistanceService StatePersistenceService { get; }
    public IAccountCalendarStateService AccountCalendarStateService { get; }
    public INavigationService NavigationService { get; }
    public WinoApplicationMode Mode => WinoApplicationMode.Calendar;
    public bool HandlesNavigationSelection => false;
    public VisibleDateRange CurrentVisibleRange => _calendarPageViewModel.CurrentVisibleRange;
    public string VisibleDateRangeText => _calendarPageViewModel.VisibleDateRangeText;
    System.Collections.IEnumerable ICalendarShellClient.GroupedAccountCalendars => AccountCalendarStateService.GroupedAccountCalendars;
    System.Collections.IEnumerable ICalendarShellClient.DateNavigationHeaderItems => DateNavigationHeaderItems;
    object IShellClient.SelectedMenuItem
    {
        get => null;
        set { }
    }
    System.Windows.Input.ICommand ICalendarShellClient.TodayClickedCommand => TodayClickedCommand;
    System.Windows.Input.ICommand ICalendarShellClient.DateClickedCommand => DateClickedCommand;
    System.Windows.Input.ICommand ICalendarShellClient.PreviousDateRangeCommand => PreviousDateRangeCommand;
    System.Windows.Input.ICommand ICalendarShellClient.NextDateRangeCommand => NextDateRangeCommand;
    System.Windows.Input.ICommand ICalendarShellClient.SyncCommand => SyncCommand;

    public bool CanSynchronizeCalendars => !AccountCalendarStateService.IsAnySynchronizationInProgress;

    public MenuItemCollection MenuItems { get; private set; }
    public MenuItemCollection FooterItems { get; private set; }

    [ObservableProperty]
    private int _selectedMenuItemIndex = -1;

    [ObservableProperty]
    private ObservableRangeCollection<string> dateNavigationHeaderItems = [];

    [ObservableProperty]
    private int _selectedDateNavigationHeaderIndex;

    public bool IsVerticalCalendar => StatePersistenceService.CalendarDisplayType == CalendarDisplayType.Month;

    [ObservableProperty]
    private bool isStoreUpdateItemVisible;

    private readonly SettingsItem _settingsItem = new();
    private readonly StoreUpdateMenuItem _storeUpdateMenuItem = new();
    private readonly SemaphoreSlim _accountCalendarUpdateSemaphoreSlim = new(1);
    private readonly CalendarPageViewModel _calendarPageViewModel;
    private readonly IMailDialogService _dialogService;
    private readonly IStoreUpdateService _storeUpdateService;
    private readonly IAccountService _accountService;
    private readonly ICalendarService _calendarService;
    private readonly IDateContextProvider _dateContextProvider;
    private bool _runtimeSubscriptionsAttached;
    private bool _hasRegisteredPersistentRecipients;
    private DateTime? _navigationDate;

    public CalendarAppShellViewModel(
        IPreferencesService preferencesService,
        IStatePersistanceService statePersistanceService,
        IAccountService accountService,
        ICalendarService calendarService,
        IAccountCalendarStateService accountCalendarStateService,
        INavigationService navigationService,
        CalendarPageViewModel calendarPageViewModel,
        IMailDialogService dialogService,
        IStoreUpdateService storeUpdateService,
        IDateContextProvider dateContextProvider)
    {
        PreferencesService = preferencesService;
        StatePersistenceService = statePersistanceService;
        AccountCalendarStateService = accountCalendarStateService;
        NavigationService = navigationService;
        _accountService = accountService;
        _calendarService = calendarService;
        _calendarPageViewModel = calendarPageViewModel;
        _dialogService = dialogService;
        _storeUpdateService = storeUpdateService;
        _dateContextProvider = dateContextProvider;

        _calendarPageViewModel.PropertyChanged += CalendarPageViewModelPropertyChanged;
        AccountCalendarStateService.PropertyChanged += AccountCalendarStateServicePropertyChanged;
    }

    protected override void OnDispatcherAssigned()
    {
        base.OnDispatcherAssigned();

        AccountCalendarStateService.Dispatcher = Dispatcher;
        MenuItems = new MenuItemCollection(Dispatcher);
        FooterItems = new MenuItemCollection(Dispatcher);
        _ = RefreshFooterItemsAsync(false);
    }

    private void CalendarPageViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarPageViewModel.CurrentVisibleRange))
        {
            OnPropertyChanged(nameof(CurrentVisibleRange));
        }

        if (e.PropertyName == nameof(CalendarPageViewModel.CurrentVisibleRange) ||
            e.PropertyName == nameof(CalendarPageViewModel.VisibleDateRangeText))
        {
            OnPropertyChanged(nameof(VisibleDateRangeText));
            UpdateDateNavigationHeaderItems();
        }
    }

    private void PrefefencesChanged(object sender, string e)
    {
        if (e != nameof(StatePersistenceService.CalendarDisplayType))
            return;

        Messenger.Send(new CalendarDisplayTypeChangedMessage(StatePersistenceService.CalendarDisplayType));
        OnPropertyChanged(nameof(IsVerticalCalendar));
        UpdateDateNavigationHeaderItems();
        NavigateCalendarDate(GetDisplayTypeSwitchDate());
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
        var navigationArgs = activationContext?.Parameter as CalendarPageNavigationArgs;

        PreferencesService.PreferenceChanged -= PreferencesServiceChanged;
        PreferencesService.PreferenceChanged += PreferencesServiceChanged;

        await RefreshFooterItemsAsync(mode == NavigationMode.New);
        UpdateDateNavigationHeaderItems();
        await InitializeAccountCalendarsAsync();
        ValidateConfiguredNewEventCalendar();

        if (navigationArgs != null)
        {
            NavigationService.Navigate(WinoPage.CalendarPage, navigationArgs);
        }
        else if (shouldRunStartupFlows || _calendarPageViewModel.CurrentVisibleRange == null)
        {
            TodayClicked();
        }
    }

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        DetachRuntimeSubscriptions();
        PreferencesService.PreferenceChanged -= PreferencesServiceChanged;
        _ = ExecuteUIThread(() =>
        {
            DateNavigationHeaderItems.Clear();
            AccountCalendarStateService.ClearGroupedAccountCalendars();
            SelectedDateNavigationHeaderIndex = -1;
        });
        _calendarPageViewModel.CleanupForShellDeactivation();
    }

    public void PrepareForShellShutdown()
    {
        DetachRuntimeSubscriptions();
        PreferencesService.PreferenceChanged -= PreferencesServiceChanged;

        if (_hasRegisteredPersistentRecipients)
        {
            UnregisterRecipients();
            _hasRegisteredPersistentRecipients = false;
        }

        DateNavigationHeaderItems.Clear();
        SelectedDateNavigationHeaderIndex = -1;
        SelectedMenuItemIndex = -1;
        MenuItems?.Clear();
        FooterItems?.Clear();
        AccountCalendarStateService.ClearGroupedAccountCalendars();
        _calendarPageViewModel.CleanupForShellDeactivation();
    }

    private void AccountCalendarStateServicePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IAccountCalendarStateService.IsAnySynchronizationInProgress))
            return;

        OnPropertyChanged(nameof(CanSynchronizeCalendars));
        SyncCommand.NotifyCanExecuteChanged();
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
            if (!GroupedAccountCalendarViewModel.SupportsCalendar(account))
                continue;

            var accountCalendars = await _calendarService.GetAccountCalendarsAsync(account.Id).ConfigureAwait(false);
            var calendarViewModels = accountCalendars.Select(calendar => new AccountCalendarViewModel(account, calendar)).ToList();
            var groupedAccountCalendarViewModel = new GroupedAccountCalendarViewModel(account, calendarViewModels);

            await Dispatcher.ExecuteOnUIThread(() =>
            {
                AccountCalendarStateService.AddGroupedAccountCalendar(groupedAccountCalendarViewModel);
            });
        }
    }

    private void NavigateCalendarDate(DateTime date)
    {
        _navigationDate = date.Date;
        ForceNavigateCalendarDate();
    }

    private void ForceNavigateCalendarDate()
    {
        var args = new CalendarPageNavigationArgs
        {
            NavigationDate = _navigationDate ?? DateTime.Now.Date
        };

        NavigationService.Navigate(WinoPage.CalendarPage, args);
        _navigationDate = null;
    }

    [RelayCommand(CanExecute = nameof(CanSynchronizeCalendars))]
    private async Task Sync()
    {
        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
        foreach (var account in accounts)
        {
            Messenger.Send(new NewCalendarSynchronizationRequested(new CalendarSynchronizationOptions
            {
                AccountId = account.Id,
                Type = CalendarSynchronizationType.Strict
            }));
        }
    }

    private DateTime GetDisplayTypeSwitchDate()
    {
        var today = _dateContextProvider.GetToday();
        var settings = PreferencesService.GetCurrentCalendarSettings();
        var referenceRange = CurrentVisibleRange
                             ?? CalendarRangeResolver.Resolve(new CalendarDisplayRequest(StatePersistenceService.CalendarDisplayType, today), settings, today);
        var targetRange = CalendarRangeResolver.ChangeDisplayType(referenceRange, StatePersistenceService.CalendarDisplayType, settings, today);
        return targetRange.AnchorDate.ToDateTime(TimeOnly.MinValue);
    }

    [RelayCommand]
    private void TodayClicked()
    {
        NavigateCalendarDate(_dateContextProvider.GetToday().ToDateTime(TimeOnly.MinValue));
    }

    [RelayCommand]
    private void PreviousDateRange()
    {
        NavigateRelativePeriod(-1);
    }

    [RelayCommand]
    private void NextDateRange()
    {
        NavigateRelativePeriod(1);
    }

    private void NavigateRelativePeriod(int direction)
    {
        var today = _dateContextProvider.GetToday();
        var settings = PreferencesService.GetCurrentCalendarSettings();
        var referenceRange = CurrentVisibleRange
                             ?? CalendarRangeResolver.Resolve(new CalendarDisplayRequest(StatePersistenceService.CalendarDisplayType, today), settings, today);
        var targetRange = CalendarRangeResolver.Navigate(referenceRange, direction, settings, today);
        NavigateCalendarDate(targetRange.AnchorDate.ToDateTime(TimeOnly.MinValue));
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
                NavigationService.Navigate(WinoPage.CalendarPreferenceSettingsPage);
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
        => NavigateCalendarDate(clickedDateArgs.ClickedDate);

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        UnregisterRecipients();

        Messenger.Register<CalendarDisplayTypeChangedMessage>(this);
        Messenger.Register<AccountRemovedMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<CalendarDisplayTypeChangedMessage>(this);
        Messenger.Unregister<AccountRemovedMessage>(this);
    }

    private void UpdateDateNavigationHeaderItems()
    {
        var headerText = VisibleDateRangeText;
        DateNavigationHeaderItems.ReplaceRange(string.IsNullOrWhiteSpace(headerText) ? [] : [headerText]);
        SelectedDateNavigationHeaderIndex = DateNavigationHeaderItems.Count > 0 ? 0 : -1;
    }

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

        if (!exists)
        {
            PreferencesService.NewEventButtonBehavior = NewEventButtonBehavior.AskEachTime;
            PreferencesService.DefaultNewEventCalendarId = null;
        }
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
