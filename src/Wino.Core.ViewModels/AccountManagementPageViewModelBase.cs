using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels.Data;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Core.ViewModels;

public abstract partial class AccountManagementPageViewModelBase : CoreBaseViewModel
{
    public ObservableCollection<IAccountProviderDetailViewModel> Accounts { get; set; } = [];
    public ObservableCollection<IAccountProviderDetailViewModel> StartupAccounts { get; } = [];

    public bool IsPurchasePanelVisible => !HasUnlimitedAccountProduct;
    public bool IsAccountCreationAlmostOnLimit => Accounts != null && Accounts.Count == FREE_ACCOUNT_COUNT - 1;
    public bool HasAccountsDefined => Accounts != null && Accounts.Any();
    public bool CanReorderAccounts => Accounts?.Sum(a => a.HoldingAccountCount) > 1;

    public string UsedAccountsString => string.Format(Translator.WinoUpgradeRemainingAccountsMessage, Accounts.Count, FREE_ACCOUNT_COUNT);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPurchasePanelVisible))]
    public partial bool HasUnlimitedAccountProduct { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAccountCreationAlmostOnLimit))]
    [NotifyPropertyChangedFor(nameof(IsPurchasePanelVisible))]
    public partial bool IsAccountCreationBlocked { get; set; }

    [ObservableProperty]
    public partial IAccountProviderDetailViewModel StartupAccount { get; set; }

    public int FREE_ACCOUNT_COUNT { get; } = Constants.FreeAccountLimit;
    protected IDialogServiceBase DialogService { get; }
    protected INavigationService NavigationService { get; }
    protected IAccountService AccountService { get; }
    protected IProviderService ProviderService { get; }
    protected IWinoBillingService BillingService { get; }
    protected IWinoAccountProfileService WinoAccountProfileService { get; }
    protected IAuthenticationProvider AuthenticationProvider { get; }
    protected IPreferencesService PreferencesService { get; }

    public AccountManagementPageViewModelBase(IDialogServiceBase dialogService,
                                              INavigationService navigationService,
                                              IAccountService accountService,
                                              IProviderService providerService,
                                              IWinoBillingService billingService,
                                              IWinoAccountProfileService winoAccountProfileService,
                                              IAuthenticationProvider authenticationProvider,
                                              IPreferencesService preferencesService)
    {
        DialogService = dialogService;
        NavigationService = navigationService;
        AccountService = accountService;
        ProviderService = providerService;
        BillingService = billingService;
        WinoAccountProfileService = winoAccountProfileService;
        AuthenticationProvider = authenticationProvider;
        PreferencesService = preferencesService;
    }

    [RelayCommand]
    private void NavigateAccountDetails(AccountProviderDetailViewModel accountDetails)
    {
        if (accountDetails?.Account == null)
            return;

        Messenger.Send(new BreadcrumbNavigationRequested(GetAccountDetailsTitle(accountDetails.Account),
                                                         WinoPage.AccountDetailsPage,
                                                         accountDetails.Account.Id));
    }

    protected void NavigateToRequestedAccountDetails(AccountDetailsNavigationContext navigationContext)
    {
        var accountDetails = FindAccountDetails(navigationContext.AccountId);
        if (accountDetails?.Account == null)
            return;

        Messenger.Send(new BreadcrumbNavigationRequested(GetAccountDetailsTitle(accountDetails.Account),
                                                         WinoPage.AccountDetailsPage,
                                                         navigationContext));
    }

    [RelayCommand]
    public async Task PurchaseUnlimitedAccountAsync()
    {
        if (await WinoAccountProfileService.GetAuthenticatedAccountAsync().ConfigureAwait(false) == null)
        {
            DialogService.InfoBarMessage(
                Translator.GeneralTitle_Warning,
                Translator.WinoAccount_Management_CheckoutSignInRequired,
                InfoBarMessageType.Warning);
            await ExecuteUIThread(() => NavigationService.Navigate(WinoPage.WinoAccountManagementPage));
            return;
        }

        if (!await BillingService.OpenCheckoutAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS).ConfigureAwait(false))
        {
            DialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                Translator.WinoAccount_Management_PurchaseStartFailed,
                InfoBarMessageType.Error);
            return;
        }

        DialogService.InfoBarMessage(
            Translator.GeneralTitle_Info,
            Translator.WinoAccount_Management_CheckoutOpened,
            InfoBarMessageType.Information);
    }

    public async Task ManageStorePurchasesAsync()
    {
        var hasUnlimitedAccountProduct = await BillingService.HasUnlimitedAccountsAsync().ConfigureAwait(false);

        await ExecuteUIThread(() =>
        {
            HasUnlimitedAccountProduct = hasUnlimitedAccountProduct;
            IsAccountCreationBlocked = !hasUnlimitedAccountProduct && Accounts.Count >= FREE_ACCOUNT_COUNT;
        });
    }

    public AccountProviderDetailViewModel GetAccountProviderDetails(MailAccount account)
    {
        var provider = ProviderService.GetProviderDetail(account.ProviderType);

        return new AccountProviderDetailViewModel(provider, account);
    }

    public abstract Task InitializeAccountsAsync();

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        Accounts.CollectionChanged -= AccountsChanged;
        Accounts.CollectionChanged += AccountsChanged;
    }

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        base.OnNavigatedFrom(mode, parameters);

        Accounts.CollectionChanged -= AccountsChanged;
    }

    private void AccountsChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasAccountsDefined));
        RefreshStartupAccounts();
        RestoreStartupAccountSelection();
    }

    private void RefreshStartupAccounts()
    {
        StartupAccounts.Clear();

        foreach (var account in Accounts.Where(IsStartupEligible))
        {
            StartupAccounts.Add(account);
        }
    }

    private void RestoreStartupAccountSelection()
    {
        if (PreferencesService.StartupEntityId is not { } startupEntityId)
            return;

        var startupAccount = Accounts.FirstOrDefault(account =>
            account.StartupEntityId == startupEntityId && IsStartupEligible(account));

        if (startupAccount == null)
            return;

        // Reordering removes and reinserts items, which can clear ComboBox.SelectedItem before the item returns.
        if (!ReferenceEquals(StartupAccount, startupAccount))
        {
            StartupAccount = startupAccount;
            return;
        }

        OnPropertyChanged(nameof(StartupAccount));
    }

    private AccountProviderDetailViewModel FindAccountDetails(Guid accountId)
    {
        foreach (var account in Accounts)
        {
            if (account is AccountProviderDetailViewModel accountDetails && accountDetails.Account.Id == accountId)
                return accountDetails;

            if (account is MergedAccountProviderDetailViewModel mergedAccountDetails)
            {
                var holdingAccount = mergedAccountDetails.HoldingAccounts
                    .FirstOrDefault(item => item.Account.Id == accountId);
                if (holdingAccount != null)
                    return holdingAccount;
            }
        }

        return null;
    }

    private static string GetAccountDetailsTitle(MailAccount account)
        => !string.IsNullOrWhiteSpace(account?.Address)
            ? string.Format(Translator.SettingsAccountDetails_NavigationTitle, account.Address)
            : account?.Name ?? Translator.AccountDetailsPage_Title;

    private static bool IsStartupEligible(IAccountProviderDetailViewModel account)
    {
        return account switch
        {
            AccountProviderDetailViewModel accountViewModel => accountViewModel.Account.IsMailAccessGranted,
            MergedAccountProviderDetailViewModel mergedAccountViewModel => mergedAccountViewModel.HoldingAccounts.Any(a => a.Account.IsMailAccessGranted),
            _ => true
        };
    }
}
