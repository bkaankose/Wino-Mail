using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.AppCenter.Crashes;
using Serilog;
using Wino.Core;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Store;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Messages.Authorization;
using Wino.Core.Messages.Navigation;
using Wino.Core.Requests;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels
{
    public partial class AccountManagementViewModel : BaseViewModel, IRecipient<ProtocolAuthorizationCallbackReceived>
    {
        public int FREE_ACCOUNT_COUNT { get; } = 3;

        private readonly IDialogService _dialogService;
        private readonly IAccountService _accountService;
        private readonly IProviderService _providerService;
        private readonly IFolderService _folderService;
        private readonly IStoreManagementService _storeManagementService;
        private readonly IPreferencesService _preferencesService;
        private readonly IAuthenticationProvider _authenticationProvider;
        private readonly IWinoSynchronizerFactory _synchronizerFactory;

        public ObservableCollection<IAccountProviderDetailViewModel> Accounts { get; set; } = [];

        public bool IsPurchasePanelVisible => !HasUnlimitedAccountProduct;
        public bool IsAccountCreationAlmostOnLimit => Accounts != null && Accounts.Count == FREE_ACCOUNT_COUNT - 1;
        public bool HasAccountsDefined => Accounts != null && Accounts.Any();
        public bool CanReorderAccounts => Accounts?.Count > 1;

        public string UsedAccountsString => string.Format(Translator.WinoUpgradeRemainingAccountsMessage, Accounts.Count, FREE_ACCOUNT_COUNT);

        [ObservableProperty]
        private IAccountProviderDetailViewModel _startupAccount;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsPurchasePanelVisible))]
        private bool hasUnlimitedAccountProduct;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsAccountCreationAlmostOnLimit))]
        [NotifyPropertyChangedFor(nameof(IsPurchasePanelVisible))]
        private bool isAccountCreationBlocked;

        public AccountManagementViewModel(IDialogService dialogService,
                                          IWinoNavigationService navigationService,
                                          IWinoSynchronizerFactory synchronizerFactory,
                                          IAccountService accountService,
                                          IProviderService providerService,
                                          IFolderService folderService,
                                          IStoreManagementService storeManagementService,
                                          IPreferencesService preferencesService,
                                          IAuthenticationProvider authenticationProvider) : base(dialogService)
        {
            _accountService = accountService;
            _synchronizerFactory = synchronizerFactory;
            _dialogService = dialogService;
            _providerService = providerService;
            _folderService = folderService;
            _storeManagementService = storeManagementService;
            _preferencesService = preferencesService;
            _authenticationProvider = authenticationProvider;
        }

        [RelayCommand]
        private void NavigateAccountDetails(AccountProviderDetailViewModel accountDetails)
        {
            Messenger.Send(new BreadcrumbNavigationRequested(accountDetails.Account.Name,
                                                             WinoPage.AccountDetailsPage,
                                                             accountDetails.Account.Id));
        }

        [RelayCommand]
        private async Task CreateMergedAccountAsync()
        {
            var linkName = await DialogService.ShowTextInputDialogAsync(string.Empty, Translator.DialogMessage_CreateLinkedAccountTitle, Translator.DialogMessage_CreateLinkedAccountMessage);

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
        private async Task PurchaseUnlimitedAccountAsync()
        {
            var purchaseResult = await _storeManagementService.PurchaseAsync(StoreProductType.UnlimitedAccounts);

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
                var providers = _providerService.GetProviderDetails();

                // Select provider.
                var accountInformationTuple = await _dialogService.ShowNewAccountMailProviderDialogAsync(providers);

                if (accountInformationTuple != null)
                {
                    creationDialog = _dialogService.GetAccountCreationDialog(accountInformationTuple.Item2);

                    var accountName = accountInformationTuple.Item1;
                    var providerType = accountInformationTuple.Item2;

                    _accountService.ExternalAuthenticationAuthenticator = _authenticationProvider.GetAuthenticator(providerType);

                    CustomServerInformation customServerInformation = null;

                    createdAccount = new MailAccount()
                    {
                        ProviderType = providerType,
                        Name = accountName,
                        Id = Guid.NewGuid()
                    };

                    creationDialog.ShowDialog();
                    creationDialog.State = AccountCreationDialogState.SigningIn;

                    TokenInformation tokenInformation = null;

                    // Custom server implementation requires more async waiting.
                    if (creationDialog is ICustomServerAccountCreationDialog customServerDialog)
                    {
                        customServerInformation = await customServerDialog.GetCustomServerInformationAsync()
                            ?? throw new AccountSetupCanceledException();

                        // At this point connection is successful.
                        // Save the server setup information and later on we'll fetch folders.

                        customServerInformation.AccountId = createdAccount.Id;

                        createdAccount.Address = customServerInformation.Address;
                        createdAccount.ServerInformation = customServerInformation;
                    }
                    else
                    {
                        // For OAuth authentications, we just generate token and assign it to the MailAccount.

                        tokenInformation = await _accountService.ExternalAuthenticationAuthenticator.GenerateTokenAsync(createdAccount, false)
                        ?? throw new AuthenticationException(Translator.Exception_TokenInfoRetrivalFailed);

                        createdAccount.Address = tokenInformation.Address;
                        tokenInformation.AccountId = createdAccount.Id;
                    }

                    await _accountService.CreateAccountAsync(createdAccount, tokenInformation, customServerInformation);

                    // Local account has been created.
                    // Create new synchronizer and start synchronization.

                    var synchronizer = _synchronizerFactory.CreateNewSynchronizer(createdAccount);

                    if (creationDialog is ICustomServerAccountCreationDialog customServerAccountCreationDialog)
                        customServerAccountCreationDialog.ShowPreparingFolders();
                    else
                        creationDialog.State = AccountCreationDialogState.PreparingFolders;

                    var options = new SynchronizationOptions()
                    {
                        AccountId = createdAccount.Id,
                        Type = SynchronizationType.FoldersOnly
                    };

                    var synchronizationResult = await synchronizer.SynchronizeAsync(options);

                    if (synchronizationResult.CompletedState != SynchronizationCompletedState.Success)
                        throw new Exception(Translator.Exception_FailedToSynchronizeFolders);

                    // Check if Inbox folder is available for the account after synchronization.
                    var isInboxAvailable = await _folderService.IsInboxAvailableForAccountAsync(createdAccount.Id);

                    if (!isInboxAvailable)
                        throw new Exception(Translator.Exception_InboxNotAvailable);

                    // Send changes to listeners.
                    ReportUIChange(new AccountCreatedMessage(createdAccount));

                    // Notify success.
                    _dialogService.InfoBarMessage(Translator.Info_AccountCreatedTitle, string.Format(Translator.Info_AccountCreatedMessage, createdAccount.Address), InfoBarMessageType.Success);
                }
            }
            catch (AccountSetupCanceledException)
            {
                // Ignore
            }
            catch (Exception ex)
            {
                Log.Error(ex, WinoErrors.AccountCreation);
                Crashes.TrackError(ex);

                _dialogService.InfoBarMessage(Translator.Info_AccountCreationFailedTitle, ex.Message, InfoBarMessageType.Error);

                // Delete account in case of failure.
                if (createdAccount != null)
                {
                    await _accountService.DeleteAccountAsync(createdAccount);
                }
            }
            finally
            {
                creationDialog?.Complete();
            }
        }

        [RelayCommand]
        private void EditMergedAccounts(MergedAccountProviderDetailViewModel mergedAccountProviderDetailViewModel)
        {
            Messenger.Send(new BreadcrumbNavigationRequested(mergedAccountProviderDetailViewModel.MergedInbox.Name,
                                                 WinoPage.MergedAccountDetailsPage,
                                                 mergedAccountProviderDetailViewModel));
        }

        [RelayCommand]
        private async Task ReorderAccountsAsync()
        {

        }

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
                _preferencesService.StartupEntityId = StartupAccount.StartupEntityId;
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

        private async Task InitializeAccountsAsync()
        {
            StartupAccount = null;

            Accounts.Clear();

            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);

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
                if (_preferencesService.StartupEntityId != null)
                {
                    StartupAccount = Accounts.FirstOrDefault(a => a.StartupEntityId == _preferencesService.StartupEntityId);
                }
            });


            await ManageStorePurchasesAsync().ConfigureAwait(false);
        }

        private async Task ManageStorePurchasesAsync()
        {
            await ExecuteUIThread(async () =>
            {
                HasUnlimitedAccountProduct = await _storeManagementService.HasProductAsync(StoreProductType.UnlimitedAccounts);

                if (!HasUnlimitedAccountProduct)
                    IsAccountCreationBlocked = Accounts.Count >= FREE_ACCOUNT_COUNT;
                else
                    IsAccountCreationBlocked = false;
            });
        }

        private AccountProviderDetailViewModel GetAccountProviderDetails(MailAccount account)
        {
            var provider = _providerService.GetProviderDetail(account.ProviderType);

            return new AccountProviderDetailViewModel(provider, account);
        }

        public void Receive(ProtocolAuthorizationCallbackReceived message)
        {
            // Authorization must be completed in account service.

            _accountService.ExternalAuthenticationAuthenticator?.ContinueAuthorization(message.AuthorizationResponseUri);
        }
    }
}
