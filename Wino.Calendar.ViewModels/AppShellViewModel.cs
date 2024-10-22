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
         IRecipient<VisibleDateRangeChangedMessage>
    {
        public IPreferencesService PreferencesService { get; }
        public IStatePersistanceService StatePersistenceService { get; }
        public INavigationService NavigationService { get; }
        public IWinoServerConnectionManager ServerConnectionManager { get; }
        public MenuItemCollection FooterItems { get; set; }
        public MenuItemCollection MenuItems { get; set; }

        /// <summary>
        /// Gets or sets the active connection status of the Wino server.
        /// </summary>
        [ObservableProperty]
        private WinoServerConnectionStatus activeConnectionStatus;

        /// <summary>
        /// Gets or sets the displayed range in the FlipView.
        /// </summary>
        [ObservableProperty]
        private DateRange visibleDateRange;

        /// <summary>
        /// Gets or sets the number of days to display in the calendar.
        /// </summary>
        [ObservableProperty]
        private int _displayDayCount = 7;

        /// <summary>
        /// Gets or sets the current display type of the calendar.
        /// </summary>
        [ObservableProperty]
        private CalendarDisplayType _currentDisplayType = CalendarDisplayType.Day;

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

            NavigationService.Navigate(WinoPage.CalendarPage);
        }

        protected override void OnDispatcherAssigned()
        {
            base.OnDispatcherAssigned();

            MenuItems = new MenuItemCollection(Dispatcher);
            FooterItems = new MenuItemCollection(Dispatcher);
        }

        private void CreateFooterItems()
        {
            FooterItems.Clear();
            FooterItems.Add(new ManageAccountsMenuItem());
            FooterItems.Add(new SettingsItem());
        }

        #region Commands

        [RelayCommand]
        private void TodayClicked()
        {

        }

        [RelayCommand]
        public void ManageAccounts()
        {
            NavigationService.Navigate(WinoPage.AccountManagementPage);
        }

        [RelayCommand]
        private Task ReconnectServerAsync() => ServerConnectionManager.ConnectAsync();

        [RelayCommand]
        private void DateClicked(CalendarViewDayClickedEventArgs clickedDate)
            => Messenger.Send(new CalendarInitializeMessage(clickedDate.BoundryDates, CurrentDisplayType, clickedDate.ClickedDate, DisplayDayCount));

        #endregion

        public void Receive(VisibleDateRangeChangedMessage message) => VisibleDateRange = message.DateRange;
    }
}
