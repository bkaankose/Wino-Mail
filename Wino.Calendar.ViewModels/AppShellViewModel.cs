using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.MenuItems;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.ViewModels
{
    public partial class AppShellViewModel : CalendarBaseViewModel,
         IRecipient<VisibleDateRangeChangedMessage>,
        IRecipient<CalendarEnableStatusChangedMessage>
    {
        public IPreferencesService PreferencesService { get; }
        public IStatePersistanceService StatePersistenceService { get; }
        public INavigationService NavigationService { get; }
        public IWinoServerConnectionManager ServerConnectionManager { get; }
        public MenuItemCollection FooterItems { get; set; }
        public MenuItemCollection MenuItems { get; set; }

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
        /// Gets or sets the displayed range in the FlipView.
        /// </summary>
        [ObservableProperty]
        private DateRange visibleDateRange;

        /// <summary>
        /// Gets or sets the number of days to display in the calendar.
        /// </summary>
        [ObservableProperty]
        private int _displayDayCount = 5;

        /// <summary>
        /// Gets or sets the current display type of the calendar.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsVerticalCalendar))]
        private CalendarDisplayType _currentDisplayType = CalendarDisplayType.Day;

        [ObservableProperty]
        private ObservableRangeCollection<string> dateNavigationHeaderItems = [];

        [ObservableProperty]
        private int _selectedDateNavigationHeaderIndex;

        public bool IsVerticalCalendar => CurrentDisplayType == CalendarDisplayType.Month;

        public AppShellViewModel(IPreferencesService preferencesService,
                                 IStatePersistanceService statePersistanceService,
                                 INavigationService navigationService,
                                 IWinoServerConnectionManager serverConnectionManager)
        {
            PreferencesService = preferencesService;
            StatePersistenceService = statePersistanceService;
            NavigationService = navigationService;
            ServerConnectionManager = serverConnectionManager;
        }

        public override void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            CreateFooterItems();
            UpdateDateNavigationHeaderItems();
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
        private void TodayClicked() => Messenger.Send(new ClickCalendarDateMessage(DateTime.Now.Date));

        [RelayCommand]
        public void ManageAccounts() => NavigationService.Navigate(WinoPage.AccountManagementPage);

        [RelayCommand]
        private Task ReconnectServerAsync() => ServerConnectionManager.ConnectAsync();

        [RelayCommand]
        private void DateClicked(CalendarViewDayClickedEventArgs clickedDate)
            => Messenger.Send(new CalendarInitializeMessage(CurrentDisplayType, clickedDate.ClickedDate, DisplayDayCount, CalendarInitInitiative.User));

        #endregion

        public void Receive(VisibleDateRangeChangedMessage message) => VisibleDateRange = message.DateRange;

        partial void OnCurrentDisplayTypeChanged(CalendarDisplayType oldValue, CalendarDisplayType newValue)
        {
            Messenger.Send(new CalendarDisplayModeChangedMessage(oldValue, newValue));
        }

        /// <summary>
        /// Sets the header navigation items based on visible date range and calendar type.
        /// </summary>
        private void UpdateDateNavigationHeaderItems()
        {
            DateNavigationHeaderItems.Clear();

            // TODO: From settings
            var testInfo = new CultureInfo("en-US");

            if (VisibleDateRange != null)
            {
                switch (CurrentDisplayType)
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
            }

            SetDateNavigationHeaderItems();
        }

        partial void OnVisibleDateRangeChanged(DateRange value) => SetDateNavigationHeaderItems();

        private void SetDateNavigationHeaderItems()
        {
            if (VisibleDateRange == null) return;

            if (DateNavigationHeaderItems.Count == 0)
            {
                UpdateDateNavigationHeaderItems();
            }

            // TODO: Year view
            var monthIndex = VisibleDateRange.GetMostVisibleMonthIndex();

            SelectedDateNavigationHeaderIndex = Math.Max(monthIndex - 1, -1);
        }

        public async void Receive(CalendarEnableStatusChangedMessage message)
            => await ExecuteUIThread(() => IsCalendarEnabled = message.IsEnabled);
    }
}
