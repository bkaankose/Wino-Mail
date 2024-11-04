using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Extensions;
using Wino.Core.MenuItems;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.Client.Navigation;

namespace Wino.Calendar.ViewModels
{
    public partial class AppShellViewModel : CalendarBaseViewModel,
        IRecipient<VisibleDateRangeChangedMessage>,
        IRecipient<CalendarEnableStatusChangedMessage>,
        IRecipient<CalendarInitializedMessage>,
        IRecipient<NavigateManageAccountsRequested>
    {
        public event EventHandler<CalendarDisplayType> DisplayTypeChanged;
        public IPreferencesService PreferencesService { get; }
        public IStatePersistanceService StatePersistenceService { get; }
        public INavigationService NavigationService { get; }
        public IWinoServerConnectionManager ServerConnectionManager { get; }
        public MenuItemCollection FooterItems { get; set; }
        public MenuItemCollection MenuItems { get; set; }

        [ObservableProperty]
        private IMenuItem _selectedMenuItem;

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

        public AppShellViewModel(IPreferencesService preferencesService,
                                 IStatePersistanceService statePersistanceService,
                                 INavigationService navigationService,
                                 IWinoServerConnectionManager serverConnectionManager)
        {
            NavigationService = navigationService;
            ServerConnectionManager = serverConnectionManager;
            PreferencesService = preferencesService;

            StatePersistenceService = statePersistanceService;
            StatePersistenceService.StatePropertyChanged += PrefefencesChanged;
        }

        private void PrefefencesChanged(object sender, string e)
        {
            if (e == nameof(StatePersistenceService.CalendarDisplayType))
            {
                DisplayTypeChanged?.Invoke(this, StatePersistenceService.CalendarDisplayType);
                OnPropertyChanged(nameof(IsVerticalCalendar));

                // Change the calendar.
                DateClicked(new CalendarViewDayClickedEventArgs(GetDisplayTypeSwitchDate()));
            }
        }

        public override void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            CreateFooterItems();
            UpdateDateNavigationHeaderItems();
        }

        partial void OnSelectedMenuItemChanged(IMenuItem oldValue, IMenuItem newValue)
        {
            if (newValue is SettingsItem)
            {
                NavigationService.Navigate(WinoPage.SettingsPage);
            }
            else if (newValue is ManageAccountsMenuItem)
            {
                NavigationService.Navigate(WinoPage.AccountManagementPage);
            }
        }

        /// <summary>
        /// When calendar type switches, we need to navigate to the most ideal date.
        /// This method returns that date.
        /// </summary>
        private DateTime GetDisplayTypeSwitchDate()
        {
            switch (StatePersistenceService.CalendarDisplayType)
            {
                case CalendarDisplayType.Day:
                    if (HighlightedDateRange.IsInRange(DateTime.Now)) return DateTime.Now.Date;

                    return HighlightedDateRange.StartDate;
                case CalendarDisplayType.Week:
                    // TODO: From settings
                    if (HighlightedDateRange.IsInRange(DateTime.Now)) return DateTime.Now.Date.GetWeekStartDateForDate(DayOfWeek.Monday);

                    return HighlightedDateRange.StartDate.GetWeekStartDateForDate(DayOfWeek.Monday);
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

        protected override void OnDispatcherAssigned()
        {
            base.OnDispatcherAssigned();

            MenuItems = new MenuItemCollection(Dispatcher);
            FooterItems = new MenuItemCollection(Dispatcher);
        }

        public override void OnPageLoaded()
        {
            base.OnPageLoaded();

            NavigationService.Navigate(WinoPage.CalendarPage, new CalendarPageNavigationArgs()
            {
                RequestDefaultNavigation = true
            });
        }

        private void CreateFooterItems()
        {
            FooterItems.Clear();
            FooterItems.Add(new ManageAccountsMenuItem());
            FooterItems.Add(new SettingsItem());
        }

        #region Commands

        [RelayCommand]
        private void TodayClicked() => Messenger.Send(new GoToCalendarDayMessage(DateTime.Now.Date));

        [RelayCommand]
        public void ManageAccounts() => NavigationService.Navigate(WinoPage.AccountManagementPage);

        [RelayCommand]
        private Task ReconnectServerAsync() => ServerConnectionManager.ConnectAsync();

        [RelayCommand]
        private void DateClicked(CalendarViewDayClickedEventArgs clickedDate)
            => Messenger.Send(new CalendarInitializeMessage(clickedDate.ClickedDate, CalendarInitInitiative.User));

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

        // Calendar page is loaded and calendar is ready to recieve render requests.
        public void Receive(CalendarInitializedMessage message) => Messenger.Send(new GoToCalendarDayMessage(DateTime.Now.Date));

        public void Receive(NavigateManageAccountsRequested message) => SelectedMenuItem = FooterItems.FirstOrDefault(a => a is ManageAccountsMenuItem);
    }
}
