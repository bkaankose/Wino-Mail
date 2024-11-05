using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Store;
using Wino.Core.ViewModels;

namespace Wino.Calendar.ViewModels
{
    public partial class AccountManagementViewModel : AccountManagementPageViewModelBase
    {
        public AccountManagementViewModel(ICalendarDialogService dialogService,
                                          IWinoServerConnectionManager winoServerConnectionManager,
                                          INavigationService navigationService,
                                          IAccountService accountService,
                                          IProviderService providerService,
                                          IStoreManagementService storeManagementService,
                                          IAuthenticationProvider authenticationProvider,
                                          IPreferencesService preferencesService) : base(dialogService, winoServerConnectionManager, navigationService, accountService, providerService, storeManagementService, authenticationProvider, preferencesService)
        {
            CalendarDialogService = dialogService;
        }

        public ICalendarDialogService CalendarDialogService { get; }

        public override Task InitializeAccountsAsync()
        {
            return Task.CompletedTask;
        }

        public override async void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            var t = await StoreManagementService.HasProductAsync(StoreProductType.UnlimitedAccounts);
        }
    }
}
