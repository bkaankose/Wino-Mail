using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.Client.Authorization;

namespace Wino.Core.ViewModels
{
    public class AccountManagementPageViewModelBase : CoreBaseViewModel, IRecipient<ProtocolAuthorizationCallbackReceived>
    {
        public int FREE_ACCOUNT_COUNT { get; } = 3;
        protected IMailDialogService DialogService { get; }
        protected IWinoServerConnectionManager WinoServerConnectionManager { get; }
        protected INavigationService NavigationService { get; }
        protected IAccountService AccountService { get; }
        protected IProviderService ProviderService { get; }
        protected IStoreManagementService StoreManagementService { get; }
        protected IAuthenticationProvider AuthenticationProvider { get; }
        protected IPreferencesService PreferencesService { get; }

        public AccountManagementPageViewModelBase(IMailDialogService dialogService,
                                                  IWinoServerConnectionManager winoServerConnectionManager,
                                                  INavigationService navigationService,
                                                  IAccountService accountService,
                                                  IProviderService providerService,
                                                  IStoreManagementService storeManagementService,
                                                  IAuthenticationProvider authenticationProvider,
                                                  IPreferencesService preferencesService)
        {
            DialogService = dialogService;
            WinoServerConnectionManager = winoServerConnectionManager;
            NavigationService = navigationService;
            AccountService = accountService;
            ProviderService = providerService;
            StoreManagementService = storeManagementService;
            AuthenticationProvider = authenticationProvider;
            PreferencesService = preferencesService;
        }

        public async void Receive(ProtocolAuthorizationCallbackReceived message)
        {
            // Authorization must be completed in the server.
            await WinoServerConnectionManager.GetResponseAsync<bool, ProtocolAuthorizationCallbackReceived>(message);
        }
    }
}
