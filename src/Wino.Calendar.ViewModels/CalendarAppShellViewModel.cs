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
    public VisibleDateRange CurrentVisibleRange => CalendarPage.CurrentVisibleRange;
    public string VisibleDateRangeText => CalendarPage.VisibleDateRangeText;
    System.Collections.IEnumerable ICalendarShellClient.GroupedAccountCalendars => AccountCalendarStateService.GroupedAccountCalendars;
    System.Collections.IEnumerable ICalendarShellClient.DateNavigationHeaderItems => DateNavigationHeaderItems;
    object IShellMenuProvider.SelectedMenuItem
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
    public partial int SelectedMenuItemIndex { get; set; } = -1;

    [ObservableProperty]
    public partial ObservableRangeCollection<string> DateNavigationHeaderItems { get; set; } = [];

    [ObservableProperty]
    public partial int SelectedDateNavigationHeaderIndex { get; set; }

    public bool IsVerticalCalendar => StatePersistenceService.CalendarDisplayType == CalendarDisplayType.Month;

    private readonly SettingsItem _settingsItem = new();
    private readonly SemaphoreSlim _accountCalendarUpdateSemaphoreSlim = new(1);
    private readonly Lazy<CalendarPageViewModel> _lazyCalendarPageViewModel;
    private bool _isCalendarPageSubscriptionAttached;
    private readonly IMailDialogService _dialogService;
    private readonly IAccountService _accountService;
    private readonly ICalendarService _calendarService;
    private readonly IDateContextProvider _dateContextProvider;
    private bool _runtimeSubscriptionsAttached;
    private bool _hasRegisteredPersistentRecipients;
    private bool _suppressDisplayTypeNavigation;
    private DateTime? _navigationDate;
    private bool _isPreparedForShellShutdown;

    public CalendarAppShellViewModel(
        IPreferencesService preferencesService,
        IStatePersistanceService statePersistanceService,
        IAccountService accountService,
        ICalendarService calendarService,
        IAccountCalendarStateService accountCalendarStateService,
        INavigationService navigationService,
        Lazy<CalendarPageViewModel> calendarPageViewModel,
        IMailDialogService dialogService,
        IDateContextProvider dateContextProvider)
    {
        PreferencesService = preferencesService;
        StatePersistenceService = statePersistanceService;
        AccountCalendarStateService = accountCalendarStateService;
        NavigationService = navigationService;
        _accountService = accountService;
        _calendarService = calendarService;
        _lazyCalendarPageViewModel = calendarPageViewModel;
        _dialogService = dialogService;
        _dateContextProvider = dateContextProvider;

        AccountCalendarStateService.PropertyChanged += AccountCalendarStateServicePropertyChanged;
    }

    /// <summary>
    /// Resolved on first use so that never opening the calendar never builds the calendar
    /// page view model and everything hanging off it.
    /// </summary>
    private CalendarPageViewModel CalendarPage
    {
        get
        {
            var calendarPageViewModel = _lazyCalendarPageViewModel.Value;

            if (!_isCalendarPageSubscriptionAttached)
            {
                calendarPageViewModel.PropertyChanged += CalendarPageViewModelPropertyChanged;
                _isCalendarPageSubscriptionAttached = true;
            }

            return calendarPageViewModel;
        }
    }

    protected override void OnDispatcherAssigned()
    {
        base.OnDispatcherAssigned();

        _isPreparedForShellShutdown = false;
        AccountCalendarStateService.Dispatcher = Dispatcher;
        MenuItems = new MenuItemCollection(Dispatcher);
        FooterItems = new MenuItemCollection(Dispatcher);
        BuildShellMenu();
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

        if (!_suppressDisplayTypeNavigation)
        {
            NavigateCalendarDate(GetDisplayTypeSwitchDate());
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
        var navigationArgs = activationContext?.Parameter as CalendarPageNavigationArgs;
        var composeArgs = activationContext?.Parameter as CalendarEventComposeNavigationArgs;

        PreferencesService.PreferenceChanged -= PreferencesServiceChanged;
        PreferencesService.PreferenceChanged += PreferencesServiceChanged;

        await RefreshFooterItemsAsync(mode == NavigationMode.New);
        UpdateDateNavigationHeaderItems();
        await InitializeAccountCalendarsAsync();
        ValidateConfiguredNewEventCalendar();

        if (composeArgs != null)
        {
            if (!await PrepareImportedComposeArgsAsync(composeArgs))
            {
                TodayClicked();
                return;
            }

            NavigationService.Navigate(WinoPage.CalendarEventComposePage, composeArgs);

            if (composeArgs.HasUnsupportedImportContent)
            {
                _dialogService.InfoBarMessage(
                    Translator.FileActivation_ImportWarningTitle,
                    Translator.FileActivation_CalendarImportWarningMessage,
                    InfoBarMessageType.Warning);
            }
        }
        else if (navigationArgs != null)
        {
            NavigationService.Navigate(WinoPage.CalendarPage, navigationArgs);
        }
        else if (shouldRunStartupFlows || CalendarPage.CurrentVisibleRange == null)
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
        CalendarPage.CleanupForShellDeactivation();
    }

    public void PrepareForShellShutdown()
    {
        if (_isPreparedForShellShutdown)
            return;

        _isPreparedForShellShutdown = true;
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

        _accountCalendarMenuItems.Clear();
        _datePickerMenuItem = null;
        ShellMenu = null;

        if (_lazyCalendarPageViewModel.IsValueCreated)
        {
            var calendarPage = _lazyCalendarPageViewModel.Value;

            if (_isCalendarPageSubscriptionAttached)
            {
                calendarPage.PropertyChanged -= CalendarPageViewModelPropertyChanged;
                _isCalendarPageSubscriptionAttached = false;
            }

            calendarPage.CleanupForShellDeactivation();
        }

        AccountCalendarStateService.Dispatcher = null;
    }

    private void AccountCalendarStateServicePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IAccountCalendarStateService.IsAnySynchronizationInProgress))
            return;

        _ = ExecuteUIThread(() =>
        {
            OnPropertyChanged(nameof(CanSynchronizeCalendars));
            SyncCommand.NotifyCanExecuteChanged();
            RefreshShellSynchronizationState();
        });
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
        DetachAccountCalendarSubscription();
        _runtimeSubscriptionsAttached = false;
    }

    private async Task RefreshFooterItemsAsync(bool showNotification)
    {
        await ExecuteUIThread(() =>
        {
            FooterItems.Clear();
        });
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
            if (accountCalendars.Count == 0)
                continue;

            var calendarViewModels = accountCalendars.Select(calendar => new AccountCalendarViewModel(account, calendar)).ToList();
            var groupedAccountCalendarViewModel = new GroupedAccountCalendarViewModel(account, calendarViewModels);

            await Dispatcher.ExecuteOnUIThread(() =>
            {
                AccountCalendarStateService.AddGroupedAccountCalendar(groupedAccountCalendarViewModel);

                // The title bar button is bound before this runs, so it has to be told that
                // there is now something to synchronize.
                RefreshShellSynchronizationState();
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
        var today = _dateContextProvider.GetToday();

        if (StatePersistenceService.CalendarDisplayType == CalendarDisplayType.WorkWeek)
        {
            var settings = PreferencesService.GetCurrentCalendarSettings();
            var todayWorkWeek = CalendarRangeResolver.Resolve(
                new CalendarDisplayRequest(CalendarDisplayType.WorkWeek, today),
                settings,
                today);

            if (!todayWorkWeek.Contains(today))
            {
                _suppressDisplayTypeNavigation = true;

                try
                {
                    StatePersistenceService.CalendarDisplayType = CalendarDisplayType.Week;
                }
                finally
                {
                    _suppressDisplayTypeNavigation = false;
                }
            }
        }

        NavigateCalendarDate(today.ToDateTime(TimeOnly.MinValue));
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
        DateNavigationHeaderItems.ReplaceRange(string.IsNullOrWhiteSpace(headerText)
            ? Array.Empty<string>()
            : new[] { headerText });
        SelectedDateNavigationHeaderIndex = DateNavigationHeaderItems.Count > 0 ? 0 : -1;
    }

    public async void Receive(CalendarDisplayTypeChangedMessage message)
    {
        await ExecuteUIThread(() =>
        {
            OnPropertyChanged(nameof(IsVerticalCalendar));
            UpdateDateNavigationHeaderItems();
        });
    }

    public async void Receive(AccountRemovedMessage message)
    {
        await InitializeAccountCalendarsAsync().ConfigureAwait(false);
        await ExecuteUIThread(ValidateConfiguredNewEventCalendar);
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

    internal async Task<bool> PrepareImportedComposeArgsAsync(CalendarEventComposeNavigationArgs composeArgs)
    {
        if (!composeArgs.RequireCalendarPickerWhenUnresolved)
            return true;

        var accounts = await _accountService.GetAccountsAsync();
        var aliasesByAccount = await Task.WhenAll(accounts.Select(async account => new
        {
            Account = account,
            Aliases = await _accountService.GetAccountAliasesAsync(account.Id)
        }));

        var hints = composeArgs.AccountAddressHints.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchedAccounts = aliasesByAccount
            .Where(entry => hints.Contains(entry.Account.Address) ||
                            entry.Aliases.Any(alias => hints.Contains(alias.AliasAddress)))
            .Select(entry => entry.Account)
            .DistinctBy(account => account.Id)
            .ToList();

        AccountCalendar pickedCalendar = null;
        if (matchedAccounts.Count == 1)
        {
            pickedCalendar = AccountCalendarStateService.AllCalendars
                .Where(calendar => calendar.Account.Id == matchedAccounts[0].Id && !calendar.IsReadOnly)
                .OrderByDescending(calendar => calendar.IsPrimary)
                .Select(calendar => calendar.AccountCalendar)
                .FirstOrDefault(calendar => calendar.IsPrimary);
        }

        if (pickedCalendar == null)
        {
            var availableGroups = AccountCalendarStateService.GroupedAccountCalendars
                .Select(group => new CalendarPickerAccountGroup
                {
                    Account = group.Account,
                    Calendars = group.AccountCalendars
                        .Select(calendar => calendar.AccountCalendar)
                        .Where(calendar => !calendar.IsReadOnly)
                        .ToList()
                })
                .Where(group => group.Calendars.Count > 0)
                .ToList();

            if (availableGroups.Count == 0)
            {
                _dialogService.InfoBarMessage(
                    Translator.CalendarEventCompose_NoCalendarsTitle,
                    Translator.FileActivation_NoWritableCalendarsMessage,
                    InfoBarMessageType.Warning);
                return false;
            }

            var pickingResult = await _dialogService.ShowSingleCalendarPickerDialogAsync(availableGroups);
            if (pickingResult.ShouldNavigateToCalendarSettings)
            {
                NavigationService.Navigate(WinoPage.CalendarPreferenceSettingsPage);
                return false;
            }

            pickedCalendar = pickingResult.PickedCalendar;
        }

        if (pickedCalendar == null)
            return false;

        composeArgs.SelectedCalendarId = pickedCalendar.Id;

        var selectedAccount = aliasesByAccount.FirstOrDefault(entry => entry.Account.Id == pickedCalendar.AccountId);
        if (selectedAccount != null)
        {
            var ownAddresses = selectedAccount.Aliases
                .Select(alias => alias.AliasAddress)
                .Append(selectedAccount.Account.Address)
                .Where(address => !string.IsNullOrWhiteSpace(address))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            composeArgs.Attendees.RemoveAll(attendee => ownAddresses.Contains(attendee.Email));
        }

        return true;
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

}
