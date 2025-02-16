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
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.Server;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

public partial class AccountManagementViewModel : AccountManagementPageViewModelBase
{
    private readonly ISpecialImapProviderConfigResolver _specialImapProviderConfigResolver;
    private readonly IImapTestService _imapTestService;
    private readonly IWinoLogger _winoLogger;

    public IMailDialogService MailDialogService { get; }

    public AccountManagementViewModel(IMailDialogService dialogService,
                                      IWinoServerConnectionManager winoServerConnectionManager,
                                      INavigationService navigationService,
                                      IAccountService accountService,
                                      ISpecialImapProviderConfigResolver specialImapProviderConfigResolver,
                                      IProviderService providerService,
                                      IImapTestService imapTestService,
                                      IStoreManagementService storeManagementService,
                                      IWinoLogger winoLogger,
                                      IAuthenticationProvider authenticationProvider,
                                      IPreferencesService preferencesService) : base(dialogService, winoServerConnectionManager, navigationService, accountService, providerService, storeManagementService, authenticationProvider, preferencesService)
    {
        MailDialogService = dialogService;
        _specialImapProviderConfigResolver = specialImapProviderConfigResolver;
        _imapTestService = imapTestService;
        _winoLogger = winoLogger;
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

        try
        {
            var providers = ProviderService.GetAvailableProviders();

            // Select provider.
            var accountCreationDialogResult = await MailDialogService.ShowAccountProviderSelectionDialogAsync(providers);

            var accountCreationCancellationTokenSource = new CancellationTokenSource();

            if (accountCreationDialogResult != null)
            {
                creationDialog = MailDialogService.GetAccountCreationDialog(accountCreationDialogResult);

                CustomServerInformation customServerInformation = null;

                createdAccount = new MailAccount()
                {
                    ProviderType = accountCreationDialogResult.ProviderType,
                    Name = accountCreationDialogResult.AccountName,
                    SpecialImapProvider = accountCreationDialogResult.SpecialImapProviderDetails?.SpecialImapProvider ?? SpecialImapProvider.None,
                    Id = Guid.NewGuid()
                };

                await creationDialog.ShowDialogAsync(accountCreationCancellationTokenSource);

                creationDialog.State = AccountCreationDialogState.SigningIn;

                string tokenInformation = string.Empty;

                // Custom server implementation requires more async waiting.
                if (creationDialog is IImapAccountCreationDialog customServerDialog)
                {
                    // Pass along the account properties and perform initial navigation on the imap frame.
                    customServerDialog.StartImapConnectionSetup(createdAccount);

                    customServerInformation = await customServerDialog.GetCustomServerInformationAsync()
                        ?? throw new AccountSetupCanceledException();

                    // At this point connection is successful.
                    // Save the server setup information and later on we'll fetch folders.

                    customServerInformation.AccountId = createdAccount.Id;

                    createdAccount.Address = customServerInformation.Address;
                    createdAccount.ServerInformation = customServerInformation;
                    createdAccount.SenderName = customServerInformation.DisplayName;
                }
                else
                {
                    // Hanle special imap providers like iCloud and Yahoo.
                    if (accountCreationDialogResult.SpecialImapProviderDetails != null)
                    {
                        // Special imap provider testing dialog. This is only available for iCloud and Yahoo.
                        customServerInformation = _specialImapProviderConfigResolver.GetServerInformation(createdAccount, accountCreationDialogResult);
                        customServerInformation.Id = Guid.NewGuid();
                        customServerInformation.AccountId = createdAccount.Id;

                        createdAccount.SenderName = accountCreationDialogResult.SpecialImapProviderDetails.SenderName;
                        createdAccount.Address = customServerInformation.Address;

                        // Let server validate the imap/smtp connection.
                        var testResultResponse = await WinoServerConnectionManager.GetResponseAsync<ImapConnectivityTestResults, ImapConnectivityTestRequested>(new ImapConnectivityTestRequested(customServerInformation, true));

                        if (!testResultResponse.IsSuccess)
                        {
                            throw new Exception($"{Translator.IMAPSetupDialog_ConnectionFailedTitle}\n{testResultResponse.Message}");
                        }
                        else if (!testResultResponse.Data.IsSuccess)
                        {
                            // Server connectivity might succeed, but result might be failed.
                            throw new ImapClientPoolException(testResultResponse.Data.FailedReason, customServerInformation, testResultResponse.Data.FailureProtocolLog);
                        }
                    }
                    else
                    {
                        // OAuth authentication is handled here.
                        // Server authenticates, returns the token info here.

                        var tokenInformationResponse = await WinoServerConnectionManager
                            .GetResponseAsync<TokenInformationEx, AuthorizationRequested>(new AuthorizationRequested(accountCreationDialogResult.ProviderType,
                                                                                                                   createdAccount,
                                                                                                                   createdAccount.ProviderType == MailProviderType.Gmail), accountCreationCancellationTokenSource.Token);

                        if (creationDialog.State == AccountCreationDialogState.Canceled)
                            throw new AccountSetupCanceledException();

                        if (!tokenInformationResponse.IsSuccess)
                            throw new Exception(tokenInformationResponse.Message);

                        createdAccount.Address = tokenInformationResponse.Data.AccountAddress;

                        tokenInformationResponse.ThrowIfFailed();
                    }
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

                    if (!string.IsNullOrEmpty(profileSynchronizationResult.ProfileInformation.AccountAddress))
                    {
                        createdAccount.Address = profileSynchronizationResult.ProfileInformation.AccountAddress;
                    }

                    await AccountService.UpdateProfileInformationAsync(createdAccount.Id, profileSynchronizationResult.ProfileInformation);
                }

                if (creationDialog is IImapAccountCreationDialog customServerAccountCreationDialog)
                    customServerAccountCreationDialog.ShowPreparingFolders();
                else
                    creationDialog.State = AccountCreationDialogState.PreparingFolders;

                // Start synchronizing folders.
                var folderSyncOptions = new MailSynchronizationOptions()
                {
                    AccountId = createdAccount.Id,
                    Type = MailSynchronizationType.FoldersOnly
                };

                var folderSynchronizationResponse = await WinoServerConnectionManager.GetResponseAsync<MailSynchronizationResult, NewMailSynchronizationRequested>(new NewMailSynchronizationRequested(folderSyncOptions, SynchronizationSource.Client));

                var folderSynchronizationResult = folderSynchronizationResponse.Data;

                if (folderSynchronizationResult == null || folderSynchronizationResult.CompletedState != SynchronizationCompletedState.Success)
                    throw new Exception(Translator.Exception_FailedToSynchronizeFolders);

                // Sync aliases if supported.
                if (createdAccount.IsAliasSyncSupported)
                {
                    // Try to synchronize aliases for the account.

                    var aliasSyncOptions = new MailSynchronizationOptions()
                    {
                        AccountId = createdAccount.Id,
                        Type = MailSynchronizationType.Alias
                    };

                    var aliasSyncResponse = await WinoServerConnectionManager.GetResponseAsync<MailSynchronizationResult, NewMailSynchronizationRequested>(new NewMailSynchronizationRequested(aliasSyncOptions, SynchronizationSource.Client));
                    var aliasSynchronizationResult = folderSynchronizationResponse.Data;

                    if (aliasSynchronizationResult.CompletedState != SynchronizationCompletedState.Success)
                        throw new Exception(Translator.Exception_FailedToSynchronizeAliases);
                }
                else
                {
                    // Create root primary alias for the account.
                    // This is only available for accounts that do not support alias synchronization.

                    await AccountService.CreateRootAliasAsync(createdAccount.Id, createdAccount.Address);
                }

                // Send changes to listeners.
                ReportUIChange(new AccountCreatedMessage(createdAccount));

                // Notify success.
                DialogService.InfoBarMessage(Translator.Info_AccountCreatedTitle, string.Format(Translator.Info_AccountCreatedMessage, createdAccount.Address), InfoBarMessageType.Success);
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

            DialogService.InfoBarMessage(Translator.Info_AccountCreationFailedTitle, testClientPoolException.Message, InfoBarMessageType.Error);
        }
        catch (ImapClientPoolException clientPoolException) when (clientPoolException.InnerException != null)
        {
            DialogService.InfoBarMessage(Translator.Info_AccountCreationFailedTitle, clientPoolException.InnerException.Message, InfoBarMessageType.Error);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create account.");

            DialogService.InfoBarMessage(Translator.Info_AccountCreationFailedTitle, ex.Message, InfoBarMessageType.Error);

            // Delete account in case of failure.
            if (createdAccount != null)
            {
                await AccountService.DeleteAccountAsync(createdAccount);
            }
        }
        finally
        {
            creationDialog?.Complete(false);
        }
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
