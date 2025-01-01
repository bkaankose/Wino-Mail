using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core.Domain.Collections;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Extensions;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.Server;

namespace Wino.Calendar.ViewModels
{
    public partial class AppShellViewModel : CalendarBaseViewModel,
        IRecipient<VisibleDateRangeChangedMessage>,
        IRecipient<CalendarEnableStatusChangedMessage>,
        IRecipient<NavigateManageAccountsRequested>,
        IRecipient<CalendarDisplayTypeChangedMessage>
    {


        public IPreferencesService PreferencesService { get; }
        public IStatePersistanceService StatePersistenceService { get; }
        public IAccountCalendarStateService AccountCalendarStateService { get; }
        public INavigationService NavigationService { get; }
        public IWinoServerConnectionManager ServerConnectionManager { get; }

        [ObservableProperty]
        private int _selectedMenuItemIndex = -1;

        [ObservableProperty]
        private bool isCalendarEnabled;

        /// <summary>
        /// Gets or sets the active connection status of the Wino server.
        /// </summary>
        [ObservableProperty]
        private WinoServerConnectionStatus activeConnectionStatus;

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

        // For updating account calendars asynchronously.
        private SemaphoreSlim _accountCalendarUpdateSemaphoreSlim = new(1);

        public AppShellViewModel(IPreferencesService preferencesService,
                                 IStatePersistanceService statePersistanceService,
                                 IAccountService accountService,
                                 ICalendarService calendarService,
                                 IAccountCalendarStateService accountCalendarStateService,
                                 INavigationService navigationService,
                                 IWinoServerConnectionManager serverConnectionManager)
        {
            _accountService = accountService;
            _calendarService = calendarService;

            AccountCalendarStateService = accountCalendarStateService;
            AccountCalendarStateService.AccountCalendarSelectionStateChanged += UpdateAccountCalendarRequested;
            AccountCalendarStateService.CollectiveAccountGroupSelectionStateChanged += AccountCalendarStateCollectivelyChanged;

            NavigationService = navigationService;
            ServerConnectionManager = serverConnectionManager;
            PreferencesService = preferencesService;

            StatePersistenceService = statePersistanceService;
            StatePersistenceService.StatePropertyChanged += PrefefencesChanged;
        }

        private void SelectedCalendarItemsChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            throw new NotImplementedException();
        }

        private void PrefefencesChanged(object sender, string e)
        {
            if (e == nameof(StatePersistenceService.CalendarDisplayType))
            {
                Messenger.Send(new CalendarDisplayTypeChangedMessage(StatePersistenceService.CalendarDisplayType));


                // Change the calendar.
                DateClicked(new CalendarViewDayClickedEventArgs(GetDisplayTypeSwitchDate()));
            }
        }

        public override async void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            UpdateDateNavigationHeaderItems();

            await InitializeAccountCalendarsAsync();

            TodayClicked();
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
            await Dispatcher.ExecuteOnUIThread(() => AccountCalendarStateService.ClearGroupedAccountCalendar());

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
            if (SelectedMenuItemIndex == -1)
            {
                var args = new CalendarPageNavigationArgs()
                {
                    NavigationDate = _navigationDate ?? DateTime.Now.Date
                };

                // Already on calendar. Just navigate.
                NavigationService.Navigate(WinoPage.CalendarPage, args);

                _navigationDate = null;
            }
            else
            {
                SelectedMenuItemIndex = -1;
            }
        }

        partial void OnSelectedMenuItemIndexChanged(int oldValue, int newValue)
        {
            switch (newValue)
            {
                case -1:
                    ForceNavigateCalendarDate();
                    break;
                case 0:
                    NavigationService.Navigate(WinoPage.AccountManagementPage);
                    break;
                case 1:
                    NavigationService.Navigate(WinoPage.SettingsPage);
                    break;
                default:
                    break;
            }
        }

        [RelayCommand]
        private void Sync()
        {
            var t = new NewCalendarSynchronizationRequested(new CalendarSynchronizationOptions()
            {
                AccountId = Guid.Parse("52fae547-0740-4aa3-8d51-519bd31278ca"),
                Type = CalendarSynchronizationType.CalendarMetadata
            }, SynchronizationSource.Client);

            Messenger.Send(t);
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
                case CalendarDisplayType.Year:
                    break;
                default:
                    break;
            }

            return DateTime.Today.Date;
        }

        private DateTime? _navigationDate;
        private readonly IAccountService _accountService;
        private readonly ICalendarService _calendarService;

        #region Commands

        [RelayCommand]
        private void TodayClicked()
        {
            _navigationDate = DateTime.Now.Date;

            ForceNavigateCalendarDate();
        }

        [RelayCommand]
        public void ManageAccounts() => NavigationService.Navigate(WinoPage.AccountManagementPage);

        [RelayCommand]
        private Task ReconnectServerAsync() => ServerConnectionManager.ConnectAsync();

        [RelayCommand]
        private void DateClicked(CalendarViewDayClickedEventArgs clickedDateArgs)
        {
            _navigationDate = clickedDateArgs.ClickedDate;

            ForceNavigateCalendarDate();
        }

        #endregion

        public void Receive(VisibleDateRangeChangedMessage message) => HighlightedDateRange = message.DateRange;

        /// <summary>
        /// Sets the header navigation items based on visible date range and calendar type.
        /// </summary>
        private void UpdateDateNavigationHeaderItems()
        {
            DateNavigationHeaderItems.Clear();

            // TODO: From settings
            var testInfo = new CultureInfo("en-US");

            switch (StatePersistenceService.CalendarDisplayType)
            {
                case CalendarDisplayType.Day:
                case CalendarDisplayType.Week:
                case CalendarDisplayType.WorkWeek:
                case CalendarDisplayType.Month:
                    DateNavigationHeaderItems.ReplaceRange(testInfo.DateTimeFormat.MonthNames);
                    break;
                case CalendarDisplayType.Year:
                    break;
                default:
                    break;
            }

            SetDateNavigationHeaderItems();
        }

        partial void OnHighlightedDateRangeChanged(DateRange value) => SetDateNavigationHeaderItems();

        private void SetDateNavigationHeaderItems()
        {
            if (HighlightedDateRange == null) return;

            if (DateNavigationHeaderItems.Count == 0)
            {
                UpdateDateNavigationHeaderItems();
            }

            // TODO: Year view
            var monthIndex = HighlightedDateRange.GetMostVisibleMonthIndex();

            SelectedDateNavigationHeaderIndex = Math.Max(monthIndex - 1, -1);
        }

        public async void Receive(CalendarEnableStatusChangedMessage message)
            => await ExecuteUIThread(() => IsCalendarEnabled = message.IsEnabled);

        public void Receive(NavigateManageAccountsRequested message) => SelectedMenuItemIndex = 1;

        public void Receive(CalendarDisplayTypeChangedMessage message)
        {
            OnPropertyChanged(nameof(IsVerticalCalendar));
        }
    }
}
