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
                                      IWinoLogger winoLogger,
                                      ISpecialImapProviderConfigResolver specialImapProviderConfigResolver,
                                      ICalDavClient calDavClient,
                                      IAuthenticationProvider authenticationProvider,
                                      IPreferencesService preferencesService) : base(dialogService, navigationService, accountService, providerService, storeManagementService, authenticationProvider, preferencesService)
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

        MailAccount createdAccount = null;
        IAccountCreationDialog creationDialog = null;
        bool creationDialogClosed = false;

        try
        {
            var providers = ProviderService.GetAvailableProviders();

            // Select provider.
            var accountCreationDialogResult = await ExecuteUIThreadTaskAsync(() => MailDialogService.ShowAccountProviderSelectionDialogAsync(providers));

            if (accountCreationDialogResult != null)
            {
                CustomServerInformation customServerInformation = null;

                createdAccount = new MailAccount()
                {
                    ProviderType = accountCreationDialogResult.ProviderType,
                    Name = accountCreationDialogResult.AccountName,
                    SpecialImapProvider = accountCreationDialogResult.SpecialImapProviderDetails?.SpecialImapProvider ?? SpecialImapProvider.None,
                    Id = Guid.NewGuid(),
                    AccountColorHex = accountCreationDialogResult.AccountColorHex,
                    IsCalendarAccessGranted = true // New accounts have calendar scopes
                };

                if (accountCreationDialogResult.ProviderType == MailProviderType.IMAP4)
                {
                    if (createdAccount.SpecialImapProvider == SpecialImapProvider.iCloud || createdAccount.SpecialImapProvider == SpecialImapProvider.Yahoo)
                    {
                        var accountCreationCancellationTokenSource = new CancellationTokenSource();
                        creationDialog = MailDialogService.GetAccountCreationDialog(accountCreationDialogResult);

                        await ExecuteUIThreadTaskAsync(() => creationDialog.ShowDialogAsync(accountCreationCancellationTokenSource));
                        await Task.Delay(500);

                        await ExecuteUIThread(() => creationDialog.State = AccountCreationDialogState.SigningIn);

                        customServerInformation = _specialImapProviderConfigResolver.GetServerInformation(createdAccount, accountCreationDialogResult)
                            ?? throw new AccountSetupCanceledException();

                        customServerInformation.Id = Guid.NewGuid();
                        customServerInformation.AccountId = createdAccount.Id;

                        createdAccount.Address = accountCreationDialogResult.SpecialImapProviderDetails.Address;
                        createdAccount.SenderName = accountCreationDialogResult.SpecialImapProviderDetails.SenderName;
                        createdAccount.IsCalendarAccessGranted = customServerInformation.CalendarSupportMode != ImapCalendarSupportMode.Disabled;
                        createdAccount.ServerInformation = customServerInformation;

                        await ValidateSpecialImapConnectivityAsync(customServerInformation).ConfigureAwait(false);
                    }
                    else
                    {
                        var completionSource = new TaskCompletionSource<ImapCalDavSetupResult>();
                        var setupContext = ImapCalDavSettingsNavigationContext.CreateForCreateMode(accountCreationDialogResult, completionSource);

                        await ExecuteUIThread(() => Messenger.Send(new BreadcrumbNavigationRequested(
                            Translator.ImapCalDavSettingsPage_TitleCreate,
                            WinoPage.ImapCalDavSettingsPage,
                            setupContext)));

                        var setupResult = await completionSource.Task.ConfigureAwait(false)
                            ?? throw new AccountSetupCanceledException();

                        customServerInformation = setupResult.ServerInformation ?? throw new AccountSetupCanceledException();
                        customServerInformation.Id = Guid.NewGuid();
                        customServerInformation.AccountId = createdAccount.Id;

                        createdAccount.Address = setupResult.EmailAddress;
                        createdAccount.SenderName = setupResult.DisplayName;
                        createdAccount.IsCalendarAccessGranted = setupResult.IsCalendarAccessGranted;
                        createdAccount.ServerInformation = customServerInformation;
                    }
                }
                else
                {
                    var accountCreationCancellationTokenSource = new CancellationTokenSource();
                    creationDialog = MailDialogService.GetAccountCreationDialog(accountCreationDialogResult);

                    await ExecuteUIThreadTaskAsync(() => creationDialog.ShowDialogAsync(accountCreationCancellationTokenSource));
                    await Task.Delay(500);

                    await ExecuteUIThread(() => creationDialog.State = AccountCreationDialogState.SigningIn);

                    // OAuth authentication is handled here.
                    // Use SynchronizationManager to handle OAuth authentication.

                    var authTokenInfo = await SynchronizationManager.Instance.HandleAuthorizationAsync(
                        accountCreationDialogResult.ProviderType,
                        createdAccount,
                        createdAccount.ProviderType == MailProviderType.Gmail);

                    bool creationCanceled = false;
                    await ExecuteUIThread(() => creationCanceled = creationDialog.State == AccountCreationDialogState.Canceled);

                    if (creationCanceled)
                        throw new AccountSetupCanceledException();

                    // Update account address with authenticated user information
                    createdAccount.Address = authTokenInfo.AccountAddress;
                }

                // Address is still doesn't have a value for API synchronizers.
                // It'll be synchronized with profile information.

                await AccountService.CreateAccountAsync(createdAccount, customServerInformation);

                // Local account has been created.

                // Sync profile information if supported.
                if (createdAccount.IsProfileInfoSyncSupported)
                {
                    // Start profile information synchronization.
                    // It's only available for Outlook and Gmail synchronizers.

                    var profileSynchronizationResult = await SynchronizationManager.Instance.SynchronizeProfileAsync(createdAccount.Id);

                    if (profileSynchronizationResult.CompletedState != SynchronizationCompletedState.Success)
                        throw new Exception(Translator.Exception_FailedToSynchronizeProfileInformation);

                    if (profileSynchronizationResult.ProfileInformation != null)
                    {
                        createdAccount.SenderName = profileSynchronizationResult.ProfileInformation.SenderName;
                        createdAccount.Base64ProfilePictureData = profileSynchronizationResult.ProfileInformation.Base64ProfilePictureData;

                        if (!string.IsNullOrEmpty(profileSynchronizationResult.ProfileInformation.AccountAddress))
                        {
                            createdAccount.Address = profileSynchronizationResult.ProfileInformation.AccountAddress;
                        }

                        await AccountService.UpdateProfileInformationAsync(createdAccount.Id, profileSynchronizationResult.ProfileInformation);
                    }
                }

                if (creationDialog != null)
                    await ExecuteUIThread(() => creationDialog.State = AccountCreationDialogState.PreparingFolders);

                var folderSynchronizationResult = await SynchronizationManager.Instance.SynchronizeFoldersAsync(createdAccount.Id);

                if (folderSynchronizationResult == null || folderSynchronizationResult.CompletedState != SynchronizationCompletedState.Success)
                    throw new Exception(Translator.Exception_FailedToSynchronizeFolders);

                if (createdAccount.IsCalendarAccessGranted)
                {
                    if (creationDialog != null)
                        await ExecuteUIThread(() => creationDialog.State = AccountCreationDialogState.CalendarMetadataFetch);

                    var calendarMetadataSynchronizationResult = await SynchronizationManager.Instance.SynchronizeCalendarAsync(new CalendarSynchronizationOptions
                    {
                        AccountId = createdAccount.Id,
                        Type = CalendarSynchronizationType.CalendarMetadata
                    });

                    if (calendarMetadataSynchronizationResult == null || calendarMetadataSynchronizationResult.CompletedState != SynchronizationCompletedState.Success)
                        throw new Exception(Translator.Exception_FailedToSynchronizeCalendarMetadata);
                }

                // Sync aliases if supported.
                if (createdAccount.IsAliasSyncSupported)
                {
                    // Try to synchronize aliases for the account.
                    var aliasSynchronizationResult = await SynchronizationManager.Instance.SynchronizeAliasesAsync(createdAccount.Id);

                    if (aliasSynchronizationResult.CompletedState != SynchronizationCompletedState.Success)
                        throw new Exception(Translator.Exception_FailedToSynchronizeAliases);
                }
                else
                {
                    // Create root primary alias for the account.
                    // This is only available for accounts that do not support alias synchronization.

                    await AccountService.CreateRootAliasAsync(createdAccount.Id, createdAccount.Address);
                }

                if (creationDialog != null)
                {
                    await ExecuteUIThread(() => creationDialog.Complete(false));
                    creationDialogClosed = true;
                }

                // Send changes to listeners.
                await ExecuteUIThread(() => ReportUIChange(new AccountCreatedMessage(createdAccount)));

                // Notify success.
                await ExecuteUIThread(() => DialogService.InfoBarMessage(Translator.Info_AccountCreatedTitle, string.Format(Translator.Info_AccountCreatedMessage, createdAccount.Address), InfoBarMessageType.Success));
            }
        }
        catch (Exception ex) when (ex.Message.Contains(nameof(GmailServiceDisabledException)))
        {
            // For Google Workspace accounts, Gmail API might be disabled by the admin.
            // Wino can't continue synchronization in this case.
            // We must notify the user about this and prevent account creation.

            await ExecuteUIThread(() => DialogService.InfoBarMessage(Translator.GmailServiceDisabled_Title, Translator.GmailServiceDisabled_Message, InfoBarMessageType.Error));

            if (createdAccount != null)
            {
                await AccountService.DeleteAccountAsync(createdAccount);
            }
        }
        catch (AccountSetupCanceledException)
        {
            // Ignore
        }
        catch (Exception ex) when (ex.Message.Contains(nameof(AccountSetupCanceledException)))
        {
            // Ignore
        }
        catch (ImapClientPoolException testClientPoolException) when (testClientPoolException.CustomServerInformation != null)
        {
            var properties = testClientPoolException.CustomServerInformation.GetConnectionProperties();

            properties.Add("ProtocolLog", testClientPoolException.ProtocolLog);
            properties.Add("DiagnosticId", PreferencesService.DiagnosticId);

            _winoLogger.TrackEvent("IMAP Test Failed", properties);

            await ExecuteUIThread(() => DialogService.InfoBarMessage(Translator.Info_AccountCreationFailedTitle, testClientPoolException.Message, InfoBarMessageType.Error));
        }
        catch (ImapClientPoolException clientPoolException) when (clientPoolException.InnerException != null)
        {
            await ExecuteUIThread(() => DialogService.InfoBarMessage(Translator.Info_AccountCreationFailedTitle, clientPoolException.InnerException.Message, InfoBarMessageType.Error));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create account.");

            await ExecuteUIThread(() => DialogService.InfoBarMessage(Translator.Info_AccountCreationFailedTitle, ex.Message, InfoBarMessageType.Error));

            // Delete account in case of failure.
            if (createdAccount != null)
            {
                await AccountService.DeleteAccountAsync(createdAccount);
            }
        }
        finally
        {
            if (creationDialog != null && !creationDialogClosed)
            {
                bool isCanceled = false;
                await ExecuteUIThread(() => isCanceled = creationDialog.State == AccountCreationDialogState.Canceled);
                await ExecuteUIThread(() => creationDialog.Complete(isCanceled));
            }
        }
    }

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
