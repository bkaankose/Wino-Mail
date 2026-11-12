using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.CardDav;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Mail.ViewModels.Helpers;
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

public partial class AccountManagementViewModel : AccountManagementPageViewModelBase,
    IRecipient<AccountLayoutChanged>
{

    private readonly IWinoAccountDataSyncService _syncService;
    private readonly IWinoLogger _winoLogger;
    private readonly ISpecialImapProviderConfigResolver _specialImapProviderConfigResolver;
    private readonly ICalDavClient _calDavClient;
    private readonly IMicrosoftStoreService _storeService;
    private readonly IWinoAppStorePurchaseService? _appStorePurchases;

    public IMailDialogService MailDialogService { get; }

    /// <summary>The account list of this page: groups with their entries, then linked accounts and accounts, in layout order.</summary>
    public ObservableCollection<IAccountProviderDetailViewModel> AccountLayoutItems { get; } = [];

    public AccountManagementViewModel(IMailDialogService dialogService,
                                      INavigationService navigationService,
                                      IAccountService accountService,
                                      IKnownImapProviderCatalog providerCatalog,
                                      IWinoBillingService billingService,
                                      IWinoAccountProfileService winoAccountProfileService,
                                      IWinoAccountDataSyncService syncService,
                                      IWinoLogger winoLogger,
                                      ISpecialImapProviderConfigResolver specialImapProviderConfigResolver,
                                      ICalDavClient calDavClient,
                                      IMicrosoftStoreService storeService,
                                      IAuthenticationProvider authenticationProvider,
                                      IPreferencesService preferencesService,
                                      IPlatformCapabilities platformCapabilities,
                                      IWinoAppStorePurchaseService? appStorePurchases = null) : base(dialogService, navigationService, accountService, providerCatalog, billingService, winoAccountProfileService, authenticationProvider, preferencesService, platformCapabilities)
    {
        MailDialogService = dialogService;
        _syncService = syncService;
        _winoLogger = winoLogger;
        _specialImapProviderConfigResolver = specialImapProviderConfigResolver;
        _calDavClient = calDavClient;
        _storeService = storeService;
        _appStorePurchases = appStorePurchases;
    }

    /// <summary>The App Store price line for Unlimited Accounts; empty outside App Store builds or until StoreKit answers.</summary>
    [ObservableProperty]
    public partial string AppStoreUnlimitedAccountsPriceText { get; set; } = string.Empty;

    public bool IsAppStoreRestoreAvailable => PlatformCapabilities.AppleAppStore && _appStorePurchases?.IsAvailable == true;

    [RelayCommand]
    private async Task RestoreAppStorePurchasesAsync()
    {
        if (!IsAppStoreRestoreAvailable || _appStorePurchases is null) return;

        await _appStorePurchases.RestoreAsync().ConfigureAwait(false);
        await ManageStorePurchasesAsync().ConfigureAwait(false);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportLocalDataCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportLocalDataCommand))]
    public partial bool IsDataTransferInProgress { get; set; }

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
    private void CreateAccountGroup()
        => Messenger.Send(new BreadcrumbNavigationRequested(Translator.AccountGroupDetailsPage_NewTitle,
                                                           WinoPage.AccountGroupDetailsPage,
                                                           new AccountGroupDetailsNavigationContext(null)));

    [RelayCommand]
    private void EditAccountGroup(AccountGroupProviderDetailViewModel accountGroup)
    {
        if (accountGroup?.Group == null)
            return;

        Messenger.Send(new BreadcrumbNavigationRequested(accountGroup.Name,
                                                         WinoPage.AccountGroupDetailsPage,
                                                         new AccountGroupDetailsNavigationContext(accountGroup.Group.Id)));
    }

    public async void Receive(AccountLayoutChanged message) => await InitializeAccountsAsync();

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        Messenger.Register<AccountLayoutChanged>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<AccountLayoutChanged>(this);
    }

    [RelayCommand]
    private async Task AddNewAccountAsync()
    {
        if (IsAccountCreationBlocked)
        {
            // Nothing is sold on this platform (Android v1): explain the limit without a purchase button.
            if (!PlatformCapabilities.Purchases)
            {
                await DialogService.ShowMessageAsync(Translator.DialogMessage_AccountLimitMessage, Translator.DialogMessage_AccountLimitTitle, WinoCustomMessageDialogIcon.Warning);
                return;
            }

            var isPurchaseClicked = await DialogService.ShowConfirmationDialogAsync(Translator.DialogMessage_AccountLimitMessage, Translator.DialogMessage_AccountLimitTitle, Translator.Buttons_Purchase);

            if (!isPurchaseClicked) return;

            await PurchaseUnlimitedAccountAsync();

            return;
        }

        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.WelcomeWizard_Step2Title,
            WinoPage.ProviderSelectionPage,
            ProviderSelectionNavigationContext.CreateForSettingsAddAccount()));
    }

    public Task StartAddNewAccountAsync() => AddNewAccountAsync();

    [RelayCommand]
    private async Task PurchaseUnlimitedAccountAsync()
    {
        if (!PlatformCapabilities.Purchases) return;

        if (!PlatformCapabilities.MicrosoftStore && !PlatformCapabilities.AppleAppStore)
        {
            await PurchaseUnlimitedAccountWithWinoAccountAsync();
            return;
        }

        var channel = await SelectPurchaseChannelAsync();

        if (channel == UnlimitedAccountsPurchaseChannel.AppleAppStore)
        {
            if (_appStorePurchases is not null &&
                await AppStoreAddOnPurchase.PurchaseAsync(_appStorePurchases, WinoAddOnProductType.UNLIMITED_ACCOUNTS, DialogService, _winoLogger).ConfigureAwait(false))
            {
                await ManageStorePurchasesAsync().ConfigureAwait(false);
            }

            return;
        }

        if (channel == UnlimitedAccountsPurchaseChannel.WinoAccount)
        {
            await PurchaseUnlimitedAccountWithWinoAccountAsync();
            return;
        }

        if (channel != UnlimitedAccountsPurchaseChannel.MicrosoftStore)
            return;

        if (await UnlimitedAccountsStorePurchase.PurchaseAsync(_storeService, DialogService, _winoLogger, PlatformCapabilities).ConfigureAwait(false))
        {
            await ManageStorePurchasesAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// App Store builds offer other channels only where Apple allows it (the United States storefront);
    /// elsewhere the App Store is the only channel and no choice is shown.
    /// </summary>
    private async Task<UnlimitedAccountsPurchaseChannel?> SelectPurchaseChannelAsync()
    {
        if (PlatformCapabilities.AppleAppStore && _appStorePurchases?.IsAvailable == true &&
            !await _appStorePurchases.IsExternalPurchaseAllowedAsync().ConfigureAwait(false))
            return UnlimitedAccountsPurchaseChannel.AppleAppStore;

        return await MailDialogService.ShowUnlimitedAccountsPurchaseChannelDialogAsync();
    }

    private async Task ValidateSpecialImapConnectivityAsync(CustomServerInformation serverInformation)
    {
        while (true)
        {
            var connectivityResult = await SynchronizationManager.Instance
                .TestImapConnectivityAsync(serverInformation)
                .ConfigureAwait(false);

            if (!connectivityResult.IsCertificateUIRequired)
            {
                if (!connectivityResult.IsSuccess)
                    throw new InvalidOperationException(connectivityResult.FailedReason ?? Translator.IMAPSetupDialog_ConnectionFailedMessage);
                break;
            }

            await ServerCertificateTrustFlow.AcceptAsync(
                connectivityResult.CertificateFailure,
                connectivityResult.FailedReason,
                serverInformation,
                serverInformation.AccountId,
                ShowCertificatePromptAsync).ConfigureAwait(false);
        }

        if (serverInformation.CalendarSupportMode != ImapCalendarSupportMode.CalDav)
            return;

        if (string.IsNullOrWhiteSpace(serverInformation.CalDavServiceUrl))
            throw new InvalidOperationException(Translator.ImapCalDavSettingsPage_CalDavUrlRequired);

        while (true)
        {
            var settings = new CalDavConnectionSettings
            {
                ServiceUri = new Uri(serverInformation.CalDavServiceUrl, UriKind.Absolute),
                Username = serverInformation.CalDavUsername,
                Password = serverInformation.CalDavPassword,
                EndpointTrust = DavEndpointTrust.For(serverInformation, MailServerProtocol.CalDav)
            };

            try
            {
                await _calDavClient.DiscoverCalendarsAsync(settings).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ServerCertificateTrustFlow.TryGetFailure(ex, out var failure))
            {
                await ServerCertificateTrustFlow.AcceptAsync(
                    failure,
                    null,
                    serverInformation,
                    serverInformation.AccountId,
                    ShowCertificatePromptAsync).ConfigureAwait(false);
            }
        }
    }

    private Task<bool> ShowCertificatePromptAsync(ServerCertificateTrustPrompt prompt)
        => ExecuteUIThreadAsync(() => MailDialogService.ShowServerCertificateTrustDialogAsync(prompt));

    [RelayCommand]
    private void EditMergedAccounts(MergedAccountProviderDetailViewModel mergedAccountProviderDetailViewModel)
    {
        Messenger.Send(new BreadcrumbNavigationRequested(mergedAccountProviderDetailViewModel.MergedInbox.Name,
                                             WinoPage.MergedAccountDetailsPage,
                                             mergedAccountProviderDetailViewModel));
    }

    [RelayCommand(CanExecute = nameof(CanReorderAccounts))]
    private Task ReorderAccountsAsync() => MailDialogService.ShowAccountReorderDialogAsync(availableAccounts: Accounts);

    [RelayCommand(CanExecute = nameof(CanTransferLocalData))]
    private async Task ExportLocalDataAsync()
    {
        try
        {
            // Write to exactly the path the platform returns. On macOS this is the URL of the save
            // panel, the only location the sandbox grants; on Windows it is the picked folder plus this name.
            var suggestedFileName = WinoAccountSyncFileExportResult.CreateFileName(DateTime.Now);
            var exportPath = await ExecuteUIThreadAsync(
                () => MailDialogService.PickFilePathAsync(suggestedFileName))
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(exportPath))
            {
                return;
            }

            await ExecuteUIThread(() => IsDataTransferInProgress = true);

            var exportResult = await _syncService.ExportToFileAsync(new(), PromptSyncSecretAsync).ConfigureAwait(false);
            await File.WriteAllBytesAsync(exportPath, exportResult.Content).ConfigureAwait(false);

            DialogService.InfoBarMessage(
                Translator.GeneralTitle_Info,
                $"{BuildExportSuccessMessage(exportResult.ExportResult)} {string.Format(Translator.WinoAccount_Management_LocalDataSaved, exportPath)}",
                InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            DialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                WinoAccountApiErrorTranslator.Describe(ex),
                InfoBarMessageType.Error);
        }
        finally
        {
            await ExecuteUIThread(() => IsDataTransferInProgress = false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanTransferLocalData))]
    private async Task ImportLocalDataAsync()
    {
        try
        {
            var fileContent = await ExecuteUIThreadAsync(
                () => MailDialogService.PickWindowsFileContentAsync(".winosnap"))
                .ConfigureAwait(false);

            if (fileContent.Length == 0)
            {
                return;
            }

            await ExecuteUIThread(() => IsDataTransferInProgress = true);

            var result = await _syncService.ImportFromFileAsync(fileContent, PromptSyncSecretAsync).ConfigureAwait(false);
            if (result.Appearance != null)
            {
                await ExecuteUIThread(() => _syncService.ApplyAppearance(result.Appearance));
            }

            await InitializeAccountsAsync().ConfigureAwait(false);

            var messageType = result.FailedPreferenceCount > 0
                ? InfoBarMessageType.Warning
                : InfoBarMessageType.Success;

            DialogService.InfoBarMessage(
                result.FailedPreferenceCount > 0 ? Translator.GeneralTitle_Warning : Translator.GeneralTitle_Info,
                BuildImportMessage(result),
                messageType);
        }
        catch (JsonException)
        {
            DialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                Translator.WinoAccount_Management_LocalDataInvalidFile,
                InfoBarMessageType.Error);
        }
        catch (Exception ex)
        {
            DialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                WinoAccountApiErrorTranslator.Describe(ex),
                InfoBarMessageType.Error);
        }
        finally
        {
            await ExecuteUIThread(() => IsDataTransferInProgress = false);
        }
    }

    private bool CanTransferLocalData() => !IsDataTransferInProgress;

    private Task<string?> PromptSyncSecretAsync(SyncSnapshotSecretRequest request)
        => ExecuteUIThreadAsync(() => MailDialogService.ShowWinoAccountSyncSecretDialogAsync(request));

    public override void OnNavigatedFrom(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedFrom(mode, parameters);

        Accounts.CollectionChanged -= AccountCollectionChanged;

        PropertyChanged -= PagePropertyChanged;
    }

    private void AccountCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        ReorderAccountsCommand.NotifyCanExecuteChanged();
    }

    private void PagePropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StartupAccount) && StartupAccount != null)
        {
            PreferencesService.StartupEntityId = StartupAccount.StartupEntityId;
        }
    }

    public override async void OnNavigatedTo(NavigationMode mode, object? parameters)
        => await InitializeAsync(mode, parameters);

    public async Task InitializeAsync(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        Accounts.CollectionChanged -= AccountCollectionChanged;
        Accounts.CollectionChanged += AccountCollectionChanged;

        await InitializeAccountsAsync();

        // Returning here from the account must not forward into it again.
        if (mode != NavigationMode.Back && parameters is AccountDetailsNavigationContext accountDetailsContext)
        {
            NavigateToRequestedAccountDetails(accountDetailsContext);
        }

        PropertyChanged -= PagePropertyChanged;
        PropertyChanged += PagePropertyChanged;
    }

    public override async Task InitializeAccountsAsync()
    {
        var accounts = await AccountService.GetAccountsAsync().ConfigureAwait(false);
        var accountGroups = await AccountService.GetAccountGroupsAsync().ConfigureAwait(false);
        var layout = AccountLayoutBuilder.Build(accounts, accountGroups);

        await ExecuteUIThread(() =>
        {
            StartupAccount = null;
            Accounts.Clear();
            AccountLayoutItems.Clear();

            // Accounts stays one flat list for the startup picker, the limits and the other platforms;
            // the account list on this page shows the groups.
            foreach (var entry in layout)
            {
                if (entry.Kind != AccountLayoutEntryKind.Group)
                {
                    var details = CreateLayoutEntryDetails(entry);
                    Accounts.Add(details);
                    AccountLayoutItems.Add(details);
                    continue;
                }

                if (entry.Group is not { } group)
                    continue;

                var groupEntries = entry.Children.Select(CreateLayoutEntryDetails).ToList();

                foreach (var groupEntry in groupEntries)
                {
                    Accounts.Add(groupEntry);
                }

                AccountLayoutItems.Add(new AccountGroupProviderDetailViewModel(group, groupEntries));
            }

            // Handle startup entity.
            if (PreferencesService.StartupEntityId != null)
            {
                StartupAccount = Accounts.FirstOrDefault(a => a.StartupEntityId == PreferencesService.StartupEntityId);
            }
        });


        await ManageStorePurchasesAsync().ConfigureAwait(false);

        if (IsAppStoreRestoreAvailable && _appStorePurchases is not null)
        {
            var price = await _appStorePurchases.GetDisplayPriceAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS).ConfigureAwait(false);
            await ExecuteUIThread(() => AppStoreUnlimitedAccountsPriceText = price is null
                ? string.Empty
                : string.Format(Translator.WinoAccount_Management_UnlimitedAppStorePrice, price));
        }
    }

    private IAccountProviderDetailViewModel CreateLayoutEntryDetails(AccountLayoutEntry entry)
    {
        if (entry.Kind == AccountLayoutEntryKind.LinkedAccount)
        {
            var mergedInbox = entry.LinkedAccount ?? new MergedInbox { Id = entry.Id, Name = string.Empty };

            return new MergedAccountProviderDetailViewModel(mergedInbox, entry.Members.Select(GetAccountProviderDetails).ToList());
        }

        return GetAccountProviderDetails(entry.Account);
    }

    private static string BuildExportSuccessMessage(Wino.Core.Domain.Models.Accounts.WinoAccountSyncExportResult result)
    {
        var parts = new Collection<string>();

        if (result.IncludedPreferences)
        {
            parts.Add(Translator.WinoAccount_Management_ExportPreferencesSucceeded);
        }

        if (result.IncludedAccounts)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ExportAccountsSucceeded, result.ExportedMailboxCount));
        }

        if (result.ExportedAccountDataCount > 0)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ExportAccountDataSucceeded, result.ExportedAccountDataCount));
        }

        if (result.ExportedAppDataCount > 0)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ExportAppDataSucceeded, result.ExportedAppDataCount));
        }

        if (parts.Count == 0)
        {
            parts.Add(Translator.WinoAccount_Management_ExportSucceeded);
        }

        return string.Join(" ", parts);
    }

    private static string BuildImportMessage(Wino.Core.Domain.Models.Accounts.WinoAccountSyncImportResult result)
    {
        var parts = new Collection<string>();

        if (result.HadRemotePreferences)
        {
            parts.Add(result.FailedPreferenceCount > 0
                ? string.Format(Translator.WinoAccount_Management_ImportPartial, result.AppliedPreferenceCount, result.FailedPreferenceCount)
                : string.Format(Translator.WinoAccount_Management_ImportPreferencesSucceeded, result.AppliedPreferenceCount));
        }

        if (result.ImportedMailboxCount > 0)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ImportAccountsSucceeded, result.ImportedMailboxCount));
        }

        if (result.SkippedDuplicateMailboxCount > 0)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ImportDuplicateAccountsSkipped_Device, result.SkippedDuplicateMailboxCount));
        }

        if (result.AppliedAccountDataCount > 0)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ImportAccountDataSucceeded, result.AppliedAccountDataCount));
        }

        if (result.AppliedAppDataCount > 0)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ImportAppDataSucceeded, result.AppliedAppDataCount));
        }

        if (parts.Count == 0)
        {
            parts.Add(Translator.WinoAccount_Management_ImportEmpty);
        }

        if (result.ImportedMailboxCount > 0)
        {
            parts.Add(Translator.WinoAccount_Management_ImportReloginReminder);
        }

        return string.Join(" ", parts);
    }
}
