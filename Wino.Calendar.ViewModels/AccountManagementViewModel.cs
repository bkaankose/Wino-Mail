using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.ViewModels;
using Wino.Messaging.Server;

namespace Wino.Calendar.ViewModels;

public partial class AccountManagementViewModel : AccountManagementPageViewModelBase
{
    private readonly IProviderService _providerService;

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
        _providerService = providerService;
    }

    public ICalendarDialogService CalendarDialogService { get; }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        await InitializeAccountsAsync();
    }

    public override async Task InitializeAccountsAsync()
    {
        Accounts.Clear();

        var accounts = await AccountService.GetAccountsAsync().ConfigureAwait(false);

        await ExecuteUIThread(() =>
        {
            foreach (var account in accounts)
            {
                var accountDetails = GetAccountProviderDetails(account);

                Accounts.Add(accountDetails);
            }
        });

        await ManageStorePurchasesAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task AddNewAccountAsync()
    {
        if (IsAccountCreationBlocked)
        {
            var isPurchaseClicked = await DialogService.ShowConfirmationDialogAsync(Translator.DialogMessage_AccountLimitMessage, Translator.DialogMessage_AccountLimitTitle, Translator.Buttons_Purchase);

            if (!isPurchaseClicked) return;

            await PurchaseUnlimitedAccountAsync();

            return;
        }

        var availableProviders = _providerService.GetAvailableProviders();

        var accountCreationDialogResult = await DialogService.ShowAccountProviderSelectionDialogAsync(availableProviders);

        if (accountCreationDialogResult == null) return;

        var accountCreationCancellationTokenSource = new CancellationTokenSource();
        var accountCreationDialog = CalendarDialogService.GetAccountCreationDialog(accountCreationDialogResult);

        await accountCreationDialog.ShowDialogAsync(accountCreationCancellationTokenSource);
        await Task.Delay(500);

        // For OAuth authentications, we just generate token and assign it to the MailAccount.

        var createdAccount = new MailAccount()
        {
            ProviderType = accountCreationDialogResult.ProviderType,
            Name = accountCreationDialogResult.AccountName,
            Id = Guid.NewGuid()
        };

        var tokenInformationResponse = await WinoServerConnectionManager
            .GetResponseAsync<TokenInformationEx, AuthorizationRequested>(new AuthorizationRequested(accountCreationDialogResult.ProviderType,
                                                                                                   createdAccount,
                                                                                                   createdAccount.ProviderType == MailProviderType.Gmail), accountCreationCancellationTokenSource.Token);

        if (accountCreationDialog.State == AccountCreationDialogState.Canceled)
            throw new AccountSetupCanceledException();

        tokenInformationResponse.ThrowIfFailed();

        await AccountService.CreateAccountAsync(createdAccount, null);

        // Sync profile information if supported.
        if (createdAccount.IsProfileInfoSyncSupported)
        {
            // Start profile information synchronization.
            // It's only available for Outlook and Gmail synchronizers.

            var profileSyncOptions = new MailSynchronizationOptions()
            {
                AccountId = createdAccount.Id,
                Type = MailSynchronizationType.UpdateProfile
            };

            var profileSynchronizationResponse = await WinoServerConnectionManager.GetResponseAsync<MailSynchronizationResult, NewMailSynchronizationRequested>(new NewMailSynchronizationRequested(profileSyncOptions, SynchronizationSource.Client));

            var profileSynchronizationResult = profileSynchronizationResponse.Data;

            if (profileSynchronizationResult.CompletedState != SynchronizationCompletedState.Success)
                throw new Exception(Translator.Exception_FailedToSynchronizeProfileInformation);

            createdAccount.SenderName = profileSynchronizationResult.ProfileInformation.SenderName;
            createdAccount.Base64ProfilePictureData = profileSynchronizationResult.ProfileInformation.Base64ProfilePictureData;

            await AccountService.UpdateProfileInformationAsync(createdAccount.Id, profileSynchronizationResult.ProfileInformation);
        }

        accountCreationDialog.State = AccountCreationDialogState.FetchingEvents;

        // Start synchronizing events.
        var synchronizationOptions = new CalendarSynchronizationOptions()
        {
            AccountId = createdAccount.Id,
            Type = CalendarSynchronizationType.CalendarMetadata
        };

        var synchronizationResponse = await WinoServerConnectionManager.GetResponseAsync<CalendarSynchronizationResult, NewCalendarSynchronizationRequested>(new NewCalendarSynchronizationRequested(synchronizationOptions, SynchronizationSource.Client));
    }
}
