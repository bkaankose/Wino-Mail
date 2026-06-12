using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
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
    public IEnumerable<IAccountProviderDetailViewModel> StartupAccounts => Accounts.Where(IsStartupEligible);

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

    public int FREE_ACCOUNT_COUNT { get; } = 3;
    protected IDialogServiceBase DialogService { get; }
    protected INavigationService NavigationService { get; }
    protected IAccountService AccountService { get; }
    protected IProviderService ProviderService { get; }
    protected IStoreManagementService StoreManagementService { get; }
    protected IWinoAccountProfileService WinoAccountProfileService { get; }
    protected IAuthenticationProvider AuthenticationProvider { get; }
    protected IPreferencesService PreferencesService { get; }

    public AccountManagementPageViewModelBase(IDialogServiceBase dialogService,
                                              INavigationService navigationService,
                                              IAccountService accountService,
                                              IProviderService providerService,
                                              IStoreManagementService storeManagementService,
                                              IWinoAccountProfileService winoAccountProfileService,
                                              IAuthenticationProvider authenticationProvider,
                                              IPreferencesService preferencesService)
    {
        DialogService = dialogService;
        NavigationService = navigationService;
        AccountService = accountService;
        ProviderService = providerService;
        StoreManagementService = storeManagementService;
        WinoAccountProfileService = winoAccountProfileService;
        AuthenticationProvider = authenticationProvider;
        PreferencesService = preferencesService;
    }

    [RelayCommand]
    private void NavigateAccountDetails(AccountProviderDetailViewModel accountDetails)
    {
        Messenger.Send(new BreadcrumbNavigationRequested(GetAccountDetailsTitle(accountDetails.Account),
                                                         WinoPage.AccountDetailsPage,
                                                         accountDetails.Account.Id));
    }

    [RelayCommand]
    public async Task PurchaseUnlimitedAccountAsync()
    {
        var purchaseResult = await StoreManagementService.PurchaseAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS);

        if (purchaseResult == StorePurchaseResult.Succeeded)
            DialogService.InfoBarMessage(Translator.Info_PurchaseThankYouTitle, Translator.Info_PurchaseThankYouMessage, InfoBarMessageType.Success);
        else if (purchaseResult == StorePurchaseResult.AlreadyPurchased)
            DialogService.InfoBarMessage(Translator.Info_PurchaseExistsTitle, Translator.Info_PurchaseExistsMessage, InfoBarMessageType.Warning);

        bool shouldRefreshPurchasePanel = purchaseResult == StorePurchaseResult.Succeeded || purchaseResult == StorePurchaseResult.AlreadyPurchased;

        if (shouldRefreshPurchasePanel)
        {
            await ManageStorePurchasesAsync();
        }
    }

    public async Task ManageStorePurchasesAsync()
    {
        var hasUnlimitedAccountProduct = await StoreManagementService.HasProductAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS).ConfigureAwait(false);

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
        OnPropertyChanged(nameof(StartupAccounts));
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
