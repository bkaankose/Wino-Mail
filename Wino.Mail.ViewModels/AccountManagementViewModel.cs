using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Services;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

public partial class AccountManagementViewModel : AccountManagementPageViewModelBase
{
    private readonly IWinoLogger _winoLogger;
    private readonly ISpecialImapProviderConfigResolver _specialImapProviderConfigResolver;
    private readonly ICalDavClient _calDavClient;

    public IMailDialogService MailDialogService { get; }

    public AccountManagementViewModel(IMailDialogService dialogService,
                                      INavigationService navigationService,
                                      IAccountService accountService,
                                      IProviderService providerService,
                                      IStoreManagementService storeManagementService,
                                      IWinoAccountProfileService winoAccountProfileService,
                                      IWinoLogger winoLogger,
                                      ISpecialImapProviderConfigResolver specialImapProviderConfigResolver,
                                      ICalDavClient calDavClient,
                                      IAuthenticationProvider authenticationProvider,
                                      IPreferencesService preferencesService) : base(dialogService, navigationService, accountService, providerService, storeManagementService, winoAccountProfileService, authenticationProvider, preferencesService)
    {
        MailDialogService = dialogService;
        _winoLogger = winoLogger;
        _specialImapProviderConfigResolver = specialImapProviderConfigResolver;
        _calDavClient = calDavClient;
    }

    [RelayCommand]
    private async Task CreateMergedAccountAsync()
    {
        var linkName = await DialogService.ShowTextInputDialogAsync(string.Empty, Translator.DialogMessage_CreateLinkedAccountTitle, Translator.DialogMessage_CreateLinkedAccountMessage, Translator.Buttons_Create);

        if (string.IsNullOrEmpty(linkName)) return;

        // Create arbitary empty merged inbox with an empty Guid and go to edit page.
        var mergedInbox = new MergedInbox()
        {
            Id = Guid.Empty,
            Name = linkName
        };

        var mergedAccountProviderDetailViewModel = new MergedAccountProviderDetailViewModel(mergedInbox, new List<AccountProviderDetailViewModel>());

        Messenger.Send(new BreadcrumbNavigationRequested(mergedAccountProviderDetailViewModel.MergedInbox.Name,
                                 WinoPage.MergedAccountDetailsPage,
                                 mergedAccountProviderDetailViewModel));
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

        Messenger.Send(new BreadcrumbNavigationRequested(Translator.WelcomeWizard_Step2Title, WinoPage.ProviderSelectionPage));
    }

    public Task StartAddNewAccountAsync() => AddNewAccountAsync();

    private async Task ValidateSpecialImapConnectivityAsync(CustomServerInformation serverInformation)
    {
        var connectivityResult = await SynchronizationManager.Instance
            .TestImapConnectivityAsync(serverInformation, allowSSLHandshake: false)
            .ConfigureAwait(false);

        if (connectivityResult.IsCertificateUIRequired)
        {
            var certificateMessage =
                $"{Translator.IMAPSetupDialog_CertificateAllowanceRequired_Row0}\n\n" +
                $"{Translator.IMAPSetupDialog_CertificateIssuer}: {connectivityResult.CertificateIssuer}\n" +
                $"{Translator.IMAPSetupDialog_CertificateValidFrom}: {connectivityResult.CertificateValidFromDateString}\n" +
                $"{Translator.IMAPSetupDialog_CertificateValidTo}: {connectivityResult.CertificateExpirationDateString}\n\n" +
                $"{Translator.IMAPSetupDialog_CertificateAllowanceRequired_Row1}";

            var allowCertificate = await ExecuteUIThreadTaskAsync(
                () => MailDialogService.ShowConfirmationDialogAsync(certificateMessage, Translator.GeneralTitle_Warning, Translator.Buttons_Allow))
                .ConfigureAwait(false);

            if (!allowCertificate)
                throw new InvalidOperationException(Translator.IMAPSetupDialog_CertificateDenied);

            connectivityResult = await SynchronizationManager.Instance
                .TestImapConnectivityAsync(serverInformation, allowSSLHandshake: true)
                .ConfigureAwait(false);
        }

        if (!connectivityResult.IsSuccess)
            throw new InvalidOperationException(connectivityResult.FailedReason ?? Translator.IMAPSetupDialog_ConnectionFailedMessage);

        if (serverInformation.CalendarSupportMode != ImapCalendarSupportMode.CalDav)
            return;

        if (string.IsNullOrWhiteSpace(serverInformation.CalDavServiceUrl))
            throw new InvalidOperationException(Translator.ImapCalDavSettingsPage_CalDavUrlRequired);

        var settings = new CalDavConnectionSettings
        {
            ServiceUri = new Uri(serverInformation.CalDavServiceUrl, UriKind.Absolute),
            Username = serverInformation.CalDavUsername,
            Password = serverInformation.CalDavPassword
        };

        await _calDavClient.DiscoverCalendarsAsync(settings).ConfigureAwait(false);
    }

    private async Task ExecuteUIThreadTaskAsync(Func<Task> action)
    {
        if (Dispatcher == null)
        {
            await action().ConfigureAwait(false);
            return;
        }

        var completionSource = new TaskCompletionSource<object>();

        await ExecuteUIThread(() =>
        {
            _ = ExecuteAndCaptureAsync();

            async Task ExecuteAndCaptureAsync()
            {
                try
                {
                    await action().ConfigureAwait(false);
                    completionSource.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    completionSource.TrySetException(ex);
                }
            }
        });

        await completionSource.Task.ConfigureAwait(false);
    }

    private async Task<T> ExecuteUIThreadTaskAsync<T>(Func<Task<T>> action)
    {
        if (Dispatcher == null)
            return await action().ConfigureAwait(false);

        var completionSource = new TaskCompletionSource<T>();

        await ExecuteUIThread(() =>
        {
            _ = ExecuteAndCaptureAsync();

            async Task ExecuteAndCaptureAsync()
            {
                try
                {
                    var result = await action().ConfigureAwait(false);
                    completionSource.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    completionSource.TrySetException(ex);
                }
            }
        });

        return await completionSource.Task.ConfigureAwait(false);
    }

    [RelayCommand]
    private void EditMergedAccounts(MergedAccountProviderDetailViewModel mergedAccountProviderDetailViewModel)
    {
        Messenger.Send(new BreadcrumbNavigationRequested(mergedAccountProviderDetailViewModel.MergedInbox.Name,
                                             WinoPage.MergedAccountDetailsPage,
                                             mergedAccountProviderDetailViewModel));
    }

    [RelayCommand(CanExecute = nameof(CanReorderAccounts))]
    private Task ReorderAccountsAsync() => MailDialogService.ShowAccountReorderDialogAsync(availableAccounts: Accounts);

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        base.OnNavigatedFrom(mode, parameters);

        Accounts.CollectionChanged -= AccountCollectionChanged;

        PropertyChanged -= PagePropertyChanged;
    }

    private void AccountCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasAccountsDefined));
        OnPropertyChanged(nameof(UsedAccountsString));
        OnPropertyChanged(nameof(IsAccountCreationAlmostOnLimit));

        ReorderAccountsCommand.NotifyCanExecuteChanged();
    }

    private void PagePropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StartupAccount) && StartupAccount != null)
        {
            PreferencesService.StartupEntityId = StartupAccount.StartupEntityId;
        }
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        Accounts.CollectionChanged -= AccountCollectionChanged;
        Accounts.CollectionChanged += AccountCollectionChanged;

        await InitializeAccountsAsync();

        PropertyChanged -= PagePropertyChanged;
        PropertyChanged += PagePropertyChanged;
    }

    public override async Task InitializeAccountsAsync()
    {
        StartupAccount = null;

        Accounts.Clear();

        var accounts = await AccountService.GetAccountsAsync().ConfigureAwait(false);

        // Group accounts and display merged ones at the top.
        var groupedAccounts = accounts.GroupBy(a => a.MergedInboxId);

        await ExecuteUIThread(() =>
        {
            foreach (var accountGroup in groupedAccounts)
            {
                var mergedInboxId = accountGroup.Key;

                if (mergedInboxId == null)
                {
                    foreach (var account in accountGroup)
                    {
                        var accountDetails = GetAccountProviderDetails(account);

                        Accounts.Add(accountDetails);
                    }
                }
                else
                {
                    var mergedInbox = accountGroup.First(a => a.MergedInboxId == mergedInboxId).MergedInbox;

                    var holdingAccountProviderDetails = accountGroup.Select(a => GetAccountProviderDetails(a)).ToList();
                    var mergedAccountViewModel = new MergedAccountProviderDetailViewModel(mergedInbox, holdingAccountProviderDetails);

                    Accounts.Add(mergedAccountViewModel);
                }
            }

            // Handle startup entity.
            if (PreferencesService.StartupEntityId != null)
            {
                StartupAccount = Accounts.FirstOrDefault(a => a.StartupEntityId == PreferencesService.StartupEntityId);
            }
        });


        await ManageStorePurchasesAsync().ConfigureAwait(false);
    }
}
