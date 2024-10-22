using System;
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

        [ObservableProperty]
        private WinoServerConnectionStatus activeConnectionStatus;

        [ObservableProperty]
        private DateRange visibleDateRange;

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
        public void ManageAccounts()
        {
            NavigationService.Navigate(WinoPage.AccountManagementPage);
        }

        [RelayCommand]
        private Task ReconnectServerAsync() => ServerConnectionManager.ConnectAsync();

        [RelayCommand]
        private void DateClicked(DateTime clickedDate)
        {

        }

        #endregion

        public void Receive(VisibleDateRangeChangedMessage message) => VisibleDateRange = message.DateRange;

    }
}
