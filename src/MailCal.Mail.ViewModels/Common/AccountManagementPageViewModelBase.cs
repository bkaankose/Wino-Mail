using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Exceptions;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Navigation;
using MailCal.Core.ViewModels.Data;
using MailCal.Mail.ViewModels.Data;
using MailCal.Messaging.Client.Navigation;

namespace MailCal.Core.ViewModels;

public abstract partial class AccountManagementPageViewModelBase : CoreBaseViewModel
{
    public ObservableCollection<IAccountProviderDetailViewModel> Accounts { get; set; } = [];
    public ObservableCollection<IAccountProviderDetailViewModel> StartupAccounts { get; } = [];

    public int UsedAccountCount => Accounts?.Sum(account => account.HoldingAccountCount) ?? 0;
    public bool HasAccountsDefined => Accounts != null && Accounts.Any();
    public bool CanReorderAccounts => Accounts?.Sum(a => a.HoldingAccountCount) > 1;

    [ObservableProperty]
    public partial IAccountProviderDetailViewModel StartupAccount { get; set; }

    protected IDialogServiceBase DialogService { get; }
    protected INavigationService NavigationService { get; }
    protected IAccountService AccountService { get; }
    protected IKnownImapProviderCatalog ProviderCatalog { get; }
    protected IAuthenticationProvider AuthenticationProvider { get; }
    protected IPreferencesService PreferencesService { get; }
    protected IPlatformCapabilities PlatformCapabilities { get; }

    public AccountManagementPageViewModelBase(IDialogServiceBase dialogService,
                                              INavigationService navigationService,
                                              IAccountService accountService,
                                              IKnownImapProviderCatalog providerCatalog,
                                              IAuthenticationProvider authenticationProvider,
                                              IPreferencesService preferencesService,
                                              IPlatformCapabilities platformCapabilities)
    {
        DialogService = dialogService;
        NavigationService = navigationService;
        AccountService = accountService;
        ProviderCatalog = providerCatalog;
        AuthenticationProvider = authenticationProvider;
        PreferencesService = preferencesService;
        PlatformCapabilities = platformCapabilities;
    }

    [RelayCommand]
    private void NavigateAccountDetails(AccountProviderDetailViewModel accountDetails)
    {
        if (accountDetails?.Account == null)
            return;

        Messenger.Send(new BreadcrumbNavigationRequested(GetAccountDetailsTitle(accountDetails.Account),
                                                         MailCalPage.AccountDetailsPage,
                                                         accountDetails.Account.Id));
    }

    protected void NavigateToRequestedAccountDetails(AccountDetailsNavigationContext navigationContext)
    {
        var accountDetails = FindAccountDetails(navigationContext.AccountId);
        if (accountDetails?.Account == null)
            return;

        Messenger.Send(new BreadcrumbNavigationRequested(GetAccountDetailsTitle(accountDetails.Account),
                                                         MailCalPage.AccountDetailsPage,
                                                         navigationContext));
    }

    public AccountProviderDetailViewModel GetAccountProviderDetails(MailAccount account)
    {
        var provider = ProviderCatalog.GetProviderDetail(account.ProviderType);

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
        OnPropertyChanged(nameof(UsedAccountCount));
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
