﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.AppCenter.Crashes;
using MoreLinq;
using MoreLinq.Extensions;
using Serilog;
using Wino.Core;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.MenuItems;
using Wino.Core.Services;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.Server;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels
{
    public partial class AppShellViewModel : BaseViewModel,
        IRecipient<NavigateSettingsRequested>,
        IRecipient<MailtoProtocolMessageRequested>,
        IRecipient<RefreshUnreadCountsMessage>,
        IRecipient<AccountsMenuRefreshRequested>,
        IRecipient<MergedInboxRenamed>,
        IRecipient<LanguageChanged>,
        IRecipient<AccountMenuItemsReordered>,
        IRecipient<AccountSynchronizationProgressUpdatedMessage>
    {
        #region Menu Items

        [ObservableProperty]
        private object selectedMenuItem;

        private IAccountMenuItem latestSelectedAccountMenuItem;

        public MenuItemCollection FooterItems { get; set; }
        public MenuItemCollection MenuItems { get; set; }

        private readonly SettingsItem SettingsItem = new SettingsItem();

        private readonly RateMenuItem RatingItem = new RateMenuItem();

        private readonly ManageAccountsMenuItem ManageAccountsMenuItem = new ManageAccountsMenuItem();

        public NewMailMenuItem CreateMailMenuItem = new NewMailMenuItem();

        #endregion

        public IStatePersistanceService StatePersistenceService { get; }
        public IWinoServerConnectionManager ServerConnectionManager { get; }
        public IPreferencesService PreferencesService { get; }
        public IWinoNavigationService NavigationService { get; }

        private readonly IFolderService _folderService;
        private readonly IAccountService _accountService;
        private readonly IContextMenuItemService _contextMenuItemService;
        private readonly IStoreRatingService _storeRatingService;
        private readonly ILaunchProtocolService _launchProtocolService;
        private readonly INotificationBuilder _notificationBuilder;
        private readonly IWinoRequestDelegator _winoRequestDelegator;

        private readonly IBackgroundTaskService _backgroundTaskService;
        private readonly IMimeFileService _mimeFileService;

        private readonly INativeAppService _nativeAppService;
        private readonly IMailService _mailService;

        private readonly SemaphoreSlim accountInitFolderUpdateSlim = new SemaphoreSlim(1);

        [ObservableProperty]
        private WinoServerConnectionStatus activeConnectionStatus;

        public AppShellViewModel(IDialogService dialogService,
                                 IWinoNavigationService navigationService,
                                 IBackgroundTaskService backgroundTaskService,
                                 IMimeFileService mimeFileService,
                                 INativeAppService nativeAppService,
                                 IMailService mailService,
                                 IAccountService accountService,
                                 IContextMenuItemService contextMenuItemService,
                                 IStoreRatingService storeRatingService,
                                 IPreferencesService preferencesService,
                                 ILaunchProtocolService launchProtocolService,
                                 INotificationBuilder notificationBuilder,
                                 IWinoRequestDelegator winoRequestDelegator,
                                 IFolderService folderService,
                                 IStatePersistanceService statePersistanceService,
                                 IWinoServerConnectionManager serverConnectionManager) : base(dialogService)
        {
            StatePersistenceService = statePersistanceService;
            ServerConnectionManager = serverConnectionManager;

            ActiveConnectionStatus = serverConnectionManager.Status;
            ServerConnectionManager.StatusChanged += async (sender, status) =>
            {
                await ExecuteUIThread(() =>
                {
                    ActiveConnectionStatus = status;
                });
            };

            PreferencesService = preferencesService;
            NavigationService = navigationService;

            _backgroundTaskService = backgroundTaskService;
            _mimeFileService = mimeFileService;
            _nativeAppService = nativeAppService;
            _mailService = mailService;
            _folderService = folderService;
            _accountService = accountService;
            _contextMenuItemService = contextMenuItemService;
            _storeRatingService = storeRatingService;
            _launchProtocolService = launchProtocolService;
            _notificationBuilder = notificationBuilder;
            _winoRequestDelegator = winoRequestDelegator;
        }

        [RelayCommand]
        private Task ReconnectServerAsync() => ServerConnectionManager.ConnectAsync();

        protected override void OnDispatcherAssigned()
        {
            base.OnDispatcherAssigned();

            MenuItems = new MenuItemCollection(Dispatcher);
            FooterItems = new MenuItemCollection(Dispatcher);
        }

        public IEnumerable<FolderOperationMenuItem> GetFolderContextMenuActions(IBaseFolderMenuItem folder)
        {
            if (folder == null || folder.SpecialFolderType == SpecialFolderType.Category || folder.SpecialFolderType == SpecialFolderType.More)
                return default;

            return _contextMenuItemService.GetFolderContextMenuActions(folder);
        }

        private async Task CreateFooterItemsAsync()
        {
            await ExecuteUIThread(() =>
            {
                // TODO: Selected footer item container still remains selected after re-creation.
                // To reproduce, go settings and change the language.

                foreach (var item in FooterItems)
                {
                    item.IsExpanded = false;
                    item.IsSelected = false;
                }

                FooterItems.Clear();

                FooterItems.Add(ManageAccountsMenuItem);
                FooterItems.Add(SettingsItem);
            });
        }

        private async Task LoadAccountsAsync()
        {
            // First clear all account menu items.
            MenuItems.RemoveRange(MenuItems.Where(a => a is IAccountMenuItem));

            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);

            // Group accounts by merged account.
            var groupedAccounts = accounts.GroupBy(a => a.MergedInboxId).OrderBy(a => a.Key != null);

            foreach (var group in groupedAccounts)
            {
                var mergedInboxId = group.Key;

                if (mergedInboxId == null)
                {
                    // Single accounts.
                    // Preserve the order while listing.

                    var orderedGroup = group.OrderBy(a => a.Order);

                    foreach (var account in orderedGroup)
                    {
                        await ExecuteUIThread(() =>
                        {
                            MenuItems.Add(new AccountMenuItem(account, null));
                        });
                    }
                }
                else
                {
                    // Merged accounts.
                    var mergedInbox = group.First().MergedInbox;
                    var mergedAccountMenuItem = new MergedAccountMenuItem(mergedInbox, group, null);

                    foreach (var accountItem in group)
                    {
                        mergedAccountMenuItem.SubMenuItems.Add(new AccountMenuItem(accountItem, mergedAccountMenuItem));
                    }

                    await ExecuteUIThread(() =>
                    {
                        MenuItems.Add(mergedAccountMenuItem);
                    });
                }
            }

            // Re-assign latest selected account menu item for containers to reflect changes better.
            // Also , this will ensure that the latest selected account is still selected after re-creation.

            if (latestSelectedAccountMenuItem != null && MenuItems.TryGetAccountMenuItem(latestSelectedAccountMenuItem.EntityId.GetValueOrDefault(), out IAccountMenuItem foundLatestSelectedAccountMenuItem))
            {
                await ExecuteUIThread(() =>
                {
                    foundLatestSelectedAccountMenuItem.IsSelected = true;
                });
            }
        }

        public override async void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            await CreateFooterItemsAsync();

            await RecreateMenuItemsAsync();
            await ProcessLaunchOptionsAsync();

            await ForceAllAccountSynchronizationsAsync();
            ConfigureBackgroundTasks();
        }

        private void ConfigureBackgroundTasks()
        {
            try
            {
                _backgroundTaskService.UnregisterAllBackgroundTask();
            }
            catch (Exception ex)
            {
                Crashes.TrackError(ex);

                DialogService.InfoBarMessage(Translator.Info_BackgroundExecutionUnknownErrorTitle, Translator.Info_BackgroundExecutionUnknownErrorMessage, InfoBarMessageType.Error);
            }
        }

        private async Task ForceAllAccountSynchronizationsAsync()
        {
            // Run Inbox synchronization for all accounts on startup.
            var accounts = await _accountService.GetAccountsAsync();

            foreach (var account in accounts)
            {
                var options = new SynchronizationOptions()
                {
                    AccountId = account.Id,
                    Type = SynchronizationType.Full
                };

                Messenger.Send(new NewSynchronizationRequested(options, SynchronizationSource.Client));
            }
        }

        // Navigate to startup account's Inbox.
        private async Task ProcessLaunchOptionsAsync()
        {
            try
            {
                // Check whether we have saved navigation item from toast.

                bool hasToastActivation = _launchProtocolService.LaunchParameter != null;

                if (hasToastActivation)
                {
                    if (_launchProtocolService.LaunchParameter is AccountMenuItemExtended accountExtendedMessage)
                    {
                        // Find the account that this folder and mail belongs to.
                        var account = await _mailService.GetMailAccountByUniqueIdAsync(accountExtendedMessage.NavigateMailItem.UniqueId).ConfigureAwait(false);

                        if (account != null && MenuItems.TryGetAccountMenuItem(account.Id, out IAccountMenuItem accountMenuItem))
                        {
                            await ChangeLoadedAccountAsync(accountMenuItem);

                            WeakReferenceMessenger.Default.Send(accountExtendedMessage);

                            _launchProtocolService.LaunchParameter = null;
                        }
                        else
                        {
                            await ProcessLaunchDefaultAsync();
                        }
                    }
                }
                else
                {
                    bool hasMailtoActivation = _launchProtocolService.MailToUri != null;

                    if (hasMailtoActivation)
                    {
                        // mailto activation. Create new mail with specific delivered address as receiver.

                        WeakReferenceMessenger.Default.Send(new MailtoProtocolMessageRequested());
                    }
                    else
                    {
                        // Use default startup extending.
                        await ProcessLaunchDefaultAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, WinoErrors.StartupAccountExtendFail);
            }
        }

        private async Task ProcessLaunchDefaultAsync()
        {
            if (PreferencesService.StartupEntityId == null)
            {
                NavigationService.Navigate(WinoPage.WelcomePage);
            }
            else
            {
                var startupEntityId = PreferencesService.StartupEntityId.Value;

                // startupEntityId is the id of the entity to be expanded on startup.
                // This can be either AccountId or MergedAccountId right now.
                // If accountId, we'll find the root account and extend Inbox folder for it.
                // If mergedAccountId, merged account's Inbox folder will be extended.

                var startupEntityMenuItem = MenuItems.FirstOrDefault(a => a.EntityId == startupEntityId);

                if (startupEntityMenuItem != null)
                {
                    startupEntityMenuItem.Expand();

                    if (startupEntityMenuItem is IAccountMenuItem startupAccountMenuItem)
                    {
                        await ChangeLoadedAccountAsync(startupAccountMenuItem);
                    }
                }
            }
        }

        public async Task NavigateFolderAsync(IBaseFolderMenuItem baseFolderMenuItem, TaskCompletionSource<bool> folderInitAwaitTask = null)
        {
            // It's already there. Don't navigate again.
            if (SelectedMenuItem == baseFolderMenuItem) return;

            await ExecuteUIThread(() =>
            {
                SelectedMenuItem = baseFolderMenuItem;
                baseFolderMenuItem.IsSelected = true;

                if (folderInitAwaitTask == null) folderInitAwaitTask = new TaskCompletionSource<bool>();

                var args = new NavigateMailFolderEventArgs(baseFolderMenuItem, folderInitAwaitTask);

                NavigationService.NavigateFolder(args);

                UpdateWindowTitleForFolder(baseFolderMenuItem);
            });

            // Wait until mail list page picks up the event and finish initialization of the mails.
            await folderInitAwaitTask.Task;
        }

        private void UpdateWindowTitleForFolder(IBaseFolderMenuItem folder)
        {
            StatePersistenceService.CoreWindowTitle = $"{folder.AssignedAccountName} - {folder.FolderName}";
        }

        private async Task NavigateSpecialFolderAsync(MailAccount account, SpecialFolderType specialFolderType, bool extendAccountMenu)
        {
            try
            {
                if (account == null) return;

                if (!MenuItems.TryGetAccountMenuItem(account.Id, out IAccountMenuItem accountMenuItem)) return;

                // First make sure to navigate to the given accounnt.

                if (latestSelectedAccountMenuItem != accountMenuItem)
                {
                    await ChangeLoadedAccountAsync(accountMenuItem, false);
                }

                // Account folders are already initialized.
                // Try to find the special folder menu item and navigate to it.

                if (latestSelectedAccountMenuItem is IMergedAccountMenuItem latestMergedAccountMenuItem)
                {
                    if (MenuItems.TryGetMergedAccountSpecialFolderMenuItem(latestSelectedAccountMenuItem.EntityId.Value, specialFolderType, out IBaseFolderMenuItem mergedFolderMenuItem))
                    {
                        await NavigateFolderAsync(mergedFolderMenuItem);
                    }
                }
                else if (latestSelectedAccountMenuItem is IAccountMenuItem latestAccountMenuItem)
                {
                    if (MenuItems.TryGetSpecialFolderMenuItem(account.Id, specialFolderType, out FolderMenuItem rootFolderMenuItem))
                    {
                        await NavigateFolderAsync(rootFolderMenuItem);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, WinoErrors.AccountNavigateInboxFail);
            }
        }

        /// <summary>
        /// Performs move operation for given items to target folder.
        /// Used with drag and drop from Shell.
        /// </summary>
        /// <param name="items">Items to move.</param>
        /// <param name="targetFolderMenuItem">Folder menu item to move to. Can be merged folder as well.</param>
        public async Task PerformMoveOperationAsync(IEnumerable<MailCopy> items, IBaseFolderMenuItem targetFolderMenuItem)
        {
            if (!items.Any() || targetFolderMenuItem == null) return;

            // User dropped mails to merged account folder.
            if (targetFolderMenuItem is IMergedAccountFolderMenuItem mergedAccountFolderMenuItem)
            {
                // Mail items must be grouped by their account and move
                // operation should be targeted towards that account's special type.
                // Multiple move packages will be created if there are multiple accounts.

                var folderSpecialType = mergedAccountFolderMenuItem.SpecialFolderType;

                var groupedByAccount = items.GroupBy(a => a.AssignedAccount.Id);

                foreach (var group in groupedByAccount)
                {
                    var accountId = group.Key;

                    // Find the target folder for this account.
                    var handlingAccountFolder = mergedAccountFolderMenuItem.HandlingFolders.FirstOrDefault(a => a.MailAccountId == accountId);

                    if (handlingAccountFolder == null)
                    {
                        Log.Warning("Failed to find the account in the merged account folder menu item for account id {AccountId}", accountId);
                        continue;
                    }

                    var package = new MailOperationPreperationRequest(MailOperation.Move, group, false, handlingAccountFolder);
                    await _winoRequestDelegator.ExecuteAsync(package);
                }
            }
            else if (targetFolderMenuItem is IFolderMenuItem singleFolderMenuItem)
            {
                // User dropped mails to a single folder.
                // Create a single move package for this folder.

                var package = new MailOperationPreperationRequest(MailOperation.Move, items, false, targetFolderMenuItem.HandlingFolders.First());

                await _winoRequestDelegator.ExecuteAsync(package);
            }
        }

        public async Task PerformFolderOperationAsync(FolderOperation operation, IBaseFolderMenuItem folderMenuItem)
        {
            if (folderMenuItem == null)
                return;

            // Ask confirmation for cleaning up the folder.
            if (operation == FolderOperation.Empty)
            {
                var result = await DialogService.ShowConfirmationDialogAsync(Translator.DialogMessage_CleanupFolderMessage, Translator.DialogMessage_CleanupFolderTitle, Translator.Buttons_Yes);

                if (!result) return;
            }

            foreach (var folder in folderMenuItem.HandlingFolders)
            {
                if (folder is MailItemFolder realFolder)
                {
                    var folderPrepRequest = new FolderOperationPreperationRequest(operation, realFolder);

                    await _winoRequestDelegator.ExecuteAsync(folderPrepRequest);
                }
            }

            // Refresh the pins.
            if (operation == FolderOperation.Pin || operation == FolderOperation.Unpin)
            {
                Messenger.Send(new AccountsMenuRefreshRequested(true));
            }
        }

        private async Task FixAccountIssuesAsync(MailAccount account)
        {
            // TODO: This area is very unclear. Needs to be rewritten with care.
            // Fix account issues are expected to not work, but may work for some cases.

            try
            {
                if (account.AttentionReason == AccountAttentionReason.InvalidCredentials)
                    await _accountService.FixTokenIssuesAsync(account.Id);
                else if (account.AttentionReason == AccountAttentionReason.MissingSystemFolderConfiguration)
                    await DialogService.HandleSystemFolderConfigurationDialogAsync(account.Id, _folderService);

                await _accountService.ClearAccountAttentionAsync(account.Id);

                DialogService.InfoBarMessage(Translator.Info_AccountIssueFixFailedTitle, Translator.Info_AccountIssueFixSuccessMessage, InfoBarMessageType.Success);
            }
            catch (Exception ex)
            {
                DialogService.InfoBarMessage(Translator.Info_AccountIssueFixFailedTitle, ex.Message, InfoBarMessageType.Error);
            }
        }

        public void NavigatePage(WinoPage winoPage)
        {
            NavigationService.Navigate(winoPage);

            StatePersistenceService.CoreWindowTitle = "Wino Mail";
        }

        public async Task MenuItemInvokedOrSelectedAsync(IMenuItem clickedMenuItem)
        {
            if (clickedMenuItem == null) return;

            // Regular menu item clicked without page navigation.
            if (clickedMenuItem is FixAccountIssuesMenuItem fixAccountItem)
            {
                await FixAccountIssuesAsync(fixAccountItem.Account);
            }
            else if (clickedMenuItem is RateMenuItem)
            {
                await _storeRatingService.LaunchStorePageForReviewAsync();
            }
            else if (clickedMenuItem is NewMailMenuItem)
            {
                await HandleCreateNewMailAsync();
            }
            else if (clickedMenuItem is IBaseFolderMenuItem baseFolderMenuItem && baseFolderMenuItem.HandlingFolders.All(a => a.IsMoveTarget))
            {
                // Don't navigate to base folders that contain non-move target folders.
                // Theory: This is a special folder like Categories or More. Don't navigate to it.

                // Prompt user rating dialog if eligible.
                _ = _storeRatingService.PromptRatingDialogAsync();

                await NavigateFolderAsync(baseFolderMenuItem);
            }
            else if (clickedMenuItem is MergedAccountMenuItem clickedMergedAccountMenuItem && latestSelectedAccountMenuItem != clickedMenuItem)
            {
                // Don't navigate to merged account if it's already selected. Preserve user's already selected folder.
                await ChangeLoadedAccountAsync(clickedMergedAccountMenuItem, true);
            }
            else if (clickedMenuItem is SettingsItem)
            {
                NavigationService.Navigate(WinoPage.SettingsPage);
            }
            else if (clickedMenuItem is ManageAccountsMenuItem)
            {
                NavigationService.Navigate(WinoPage.AccountManagementPage);
            }
            else if (clickedMenuItem is IAccountMenuItem clickedAccountMenuItem && latestSelectedAccountMenuItem != clickedAccountMenuItem)
            {
                await ChangeLoadedAccountAsync(clickedAccountMenuItem);
            }
        }

        private async Task ChangeLoadedAccountAsync(IAccountMenuItem clickedBaseAccountMenuItem, bool navigateInbox = true)
        {
            if (clickedBaseAccountMenuItem == null) return;

            // User clicked an account in Windows Mail style menu.
            // List folders for this account and select Inbox.

            await MenuItems.SetAccountMenuItemEnabledStatusAsync(false);

            await ExecuteUIThread(() =>
            {
                clickedBaseAccountMenuItem.IsEnabled = false;

                if (latestSelectedAccountMenuItem != null)
                {
                    latestSelectedAccountMenuItem.IsSelected = false;
                }

                clickedBaseAccountMenuItem.IsSelected = true;

                latestSelectedAccountMenuItem = clickedBaseAccountMenuItem;
            });

            // Load account folder structure and replace the visible folders.
            var folders = await _folderService.GetAccountFoldersForDisplayAsync(clickedBaseAccountMenuItem);

            await MenuItems.ReplaceFoldersAsync(folders);
            await UpdateUnreadItemCountAsync();
            await MenuItems.SetAccountMenuItemEnabledStatusAsync(true);

            if (navigateInbox)
            {
                await Task.Yield();
                await NavigateInboxAsync(clickedBaseAccountMenuItem);
            }
        }

        private async Task UpdateUnreadItemCountAsync()
        {
            // Get visible account menu items, ordered by merged accounts at the last.
            // We will update the unread counts for all single accounts and trigger UI refresh for merged menu items.
            var accountMenuItems = MenuItems.GetAllAccountMenuItems().OrderBy(a => a.HoldingAccounts.Count());

            // Individually get all single accounts' unread counts.
            var accountIds = accountMenuItems.OfType<AccountMenuItem>().Select(a => a.AccountId);
            var unreadCountResult = await _folderService.GetUnreadItemCountResultsAsync(accountIds).ConfigureAwait(false);

            // Recursively update all folders' unread counts to 0.
            // Query above only returns unread counts that exists. We need to reset the rest to 0 first.

            await ExecuteUIThread(() =>
            {
                MenuItems.UpdateUnreadItemCountsToZero();
            });

            foreach (var accountMenuItem in accountMenuItems)
            {
                if (accountMenuItem is MergedAccountMenuItem mergedAccountMenuItem)
                {
                    await ExecuteUIThread(() =>
                    {
                        mergedAccountMenuItem.RefreshFolderItemCount();
                    });
                }
                else
                {
                    await ExecuteUIThread(() =>
                    {
                        accountMenuItem.UnreadItemCount = unreadCountResult
                        .Where(a => a.AccountId == accountMenuItem.HoldingAccounts.First().Id && a.SpecialFolderType == SpecialFolderType.Inbox)
                        .Sum(a => a.UnreadItemCount);
                    });
                }
            }

            // Try to update unread counts for all folders.
            foreach (var unreadCount in unreadCountResult)
            {
                if (MenuItems.TryGetFolderMenuItem(unreadCount.FolderId, out IBaseFolderMenuItem folderMenuItem))
                {
                    if (folderMenuItem is IMergedAccountFolderMenuItem mergedAccountFolderMenuItem)
                    {
                        await ExecuteUIThread(() =>
                        {
                            folderMenuItem.UnreadItemCount = unreadCountResult.Where(a => a.SpecialFolderType == unreadCount.SpecialFolderType && mergedAccountFolderMenuItem.HandlingFolders.Select(b => b.Id).Contains(a.FolderId)).Sum(a => a.UnreadItemCount);
                        });
                    }
                    else
                    {
                        await ExecuteUIThread(() =>
                        {
                            folderMenuItem.UnreadItemCount = unreadCount.UnreadItemCount;
                        });
                    }
                }
            }

            // Update unread badge after all unread counts are updated.
            await _notificationBuilder.UpdateTaskbarIconBadgeAsync();
        }

        private async Task NavigateInboxAsync(IAccountMenuItem clickedBaseAccountMenuItem)
        {
            var folderInitAwaitTask = new TaskCompletionSource<bool>();

            if (clickedBaseAccountMenuItem is AccountMenuItem accountMenuItem)
            {
                if (MenuItems.TryGetWindowsStyleRootSpecialFolderMenuItem(accountMenuItem.AccountId, SpecialFolderType.Inbox, out FolderMenuItem inboxFolder))
                {
                    await NavigateFolderAsync(inboxFolder, folderInitAwaitTask);
                }
            }
            else if (clickedBaseAccountMenuItem is MergedAccountMenuItem mergedAccountMenuItem)
            {
                if (MenuItems.TryGetMergedAccountSpecialFolderMenuItem(mergedAccountMenuItem.EntityId.GetValueOrDefault(), SpecialFolderType.Inbox, out IBaseFolderMenuItem inboxFolder))
                {
                    await NavigateFolderAsync(inboxFolder, folderInitAwaitTask);
                }
            }
        }

        public async Task HandleCreateNewMailAsync()
        {
            _ = _storeRatingService.PromptRatingDialogAsync();

            MailAccount operationAccount = null;

            // Check whether we have active folder item selected for any account.
            // We have selected account. New mail creation should be targeted for this account.

            if (SelectedMenuItem is FolderMenuItem selectedFolderMenuItem)
            {
                operationAccount = selectedFolderMenuItem.ParentAccount;
            }

            // We couldn't find any account so far.
            // If there is only 1 account to use, use it. If not,
            // send a message for flyout so user can pick from it.

            if (operationAccount == null)
            {
                // No selected account.
                // List all accounts and let user pick one.

                var accounts = await _accountService.GetAccountsAsync();

                if (!accounts.Any())
                {
                    await DialogService.ShowMessageAsync(Translator.DialogMessage_NoAccountsForCreateMailMessage, Translator.DialogMessage_NoAccountsForCreateMailTitle);
                    return;
                }

                if (accounts.Count() == 1)
                    operationAccount = accounts.FirstOrDefault();
                else
                {
                    // There are multiple accounts and there is no selection.
                    // Don't list all accounts, but only accounts that belong to Merged Inbox.

                    if (latestSelectedAccountMenuItem is MergedAccountMenuItem selectedMergedAccountMenuItem)
                    {
                        var mergedAccounts = accounts.Where(a => a.MergedInboxId == selectedMergedAccountMenuItem.EntityId);

                        if (!mergedAccounts.Any()) return;

                        Messenger.Send(new CreateNewMailWithMultipleAccountsRequested(mergedAccounts.ToList()));
                    }
                }
            }

            if (operationAccount != null)
                await CreateNewMailForAsync(operationAccount);
        }

        public async Task CreateNewMailForAsync(MailAccount account)
        {
            if (account == null) return;

            // Find draft folder.
            var draftFolder = await _folderService.GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Draft);

            if (draftFolder == null)
            {
                DialogService.InfoBarMessage(Translator.Info_DraftFolderMissingTitle,
                                             Translator.Info_DraftFolderMissingMessage,
                                             InfoBarMessageType.Error,
                                             Translator.SettingConfigureSpecialFolders_Button,
                                             () =>
                                             {
                                                 DialogService.HandleSystemFolderConfigurationDialogAsync(account.Id, _folderService);
                                             });
                return;
            }

            // Navigate to draft folder.
            await NavigateSpecialFolderAsync(account, SpecialFolderType.Draft, true);

            // Generate empty mime message.
            var draftOptions = new DraftCreationOptions
            {
                Reason = DraftCreationReason.Empty,
                MailToUri = _launchProtocolService.MailToUri
            };

            var (draftMailCopy, draftBase64MimeMessage) = await _mailService.CreateDraftAsync(account.Id, draftOptions).ConfigureAwait(false);

            var draftPreparationRequest = new DraftPreparationRequest(account, draftMailCopy, draftBase64MimeMessage);
            await _winoRequestDelegator.ExecuteAsync(draftPreparationRequest);
        }

        protected override async void OnAccountUpdated(MailAccount updatedAccount)
        {
            await ExecuteUIThread(() =>
            {
                if (MenuItems.TryGetAccountMenuItem(updatedAccount.Id, out IAccountMenuItem foundAccountMenuItem))
                {
                    foundAccountMenuItem.UpdateAccount(updatedAccount);
                }
            });
        }

        protected override void OnAccountRemoved(MailAccount removedAccount)
            => Messenger.Send(new AccountsMenuRefreshRequested(true));

        protected override async void OnAccountCreated(MailAccount createdAccount)
        {
            await RecreateMenuItemsAsync();

            if (!MenuItems.TryGetAccountMenuItem(createdAccount.Id, out IAccountMenuItem createdMenuItem)) return;

            await ChangeLoadedAccountAsync(createdMenuItem);

            // Each created account should start a new synchronization automatically.
            var options = new SynchronizationOptions()
            {
                AccountId = createdAccount.Id,
                Type = SynchronizationType.Full,
            };

            Messenger.Send(new NewSynchronizationRequested(options, SynchronizationSource.Client));

            await _nativeAppService.PinAppToTaskbarAsync();
        }

        // TODO: Handle by messaging.
        private async Task SetAccountAttentionAsync(Guid accountId, AccountAttentionReason reason)
        {
            if (!MenuItems.TryGetAccountMenuItem(accountId, out IAccountMenuItem accountMenuItem)) return;

            var accountModel = accountMenuItem.HoldingAccounts.First(a => a.Id == accountId);

            accountModel.AttentionReason = reason;

            await _accountService.UpdateAccountAsync(accountModel);

            accountMenuItem.UpdateAccount(accountModel);
        }

        public void Receive(NavigateSettingsRequested message) => SelectedMenuItem = ManageAccountsMenuItem;

        public async void Receive(MailtoProtocolMessageRequested message)
        {
            var accounts = await _accountService.GetAccountsAsync();

            MailAccount targetAccount = null;

            if (!accounts.Any())
            {
                await DialogService.ShowMessageAsync(Translator.DialogMessage_NoAccountsForCreateMailMessage, Translator.DialogMessage_NoAccountsForCreateMailTitle);
            }
            else if (accounts.Count == 1)
            {
                targetAccount = accounts[0];
            }
            else
            {
                // User must pick an account.

                targetAccount = await DialogService.ShowAccountPickerDialogAsync(accounts);
            }

            if (targetAccount == null) return;

            await CreateNewMailForAsync(targetAccount);
        }

        private async Task RecreateMenuItemsAsync()
        {
            await ExecuteUIThread(() =>
            {
                MenuItems.Clear();
                MenuItems.Add(CreateMailMenuItem);
            });

            await LoadAccountsAsync();
        }

        public async void Receive(RefreshUnreadCountsMessage message)
            => await UpdateUnreadItemCountAsync();

        public async void Receive(AccountsMenuRefreshRequested message)
        {
            await RecreateMenuItemsAsync();

            if (message.AutomaticallyNavigateFirstItem)
            {
                if (MenuItems.FirstOrDefault(a => a is IAccountMenuItem) is IAccountMenuItem firstAccount)
                {
                    await ChangeLoadedAccountAsync(firstAccount);
                }
            }
        }

        public async void Receive(MergedInboxRenamed message)
        {
            var mergedInboxMenuItem = MenuItems.FirstOrDefault(a => a.EntityId == message.MergedInboxId);

            if (mergedInboxMenuItem == null) return;

            if (mergedInboxMenuItem is MergedAccountMenuItem mergedAccountMenuItemCasted)
            {
                await ExecuteUIThread(() => { mergedAccountMenuItemCasted.MergedAccountName = message.NewName; });
            }
        }

        public async void Receive(LanguageChanged message)
        {
            await CreateFooterItemsAsync();
            await RecreateMenuItemsAsync();

            await ChangeLoadedAccountAsync(latestSelectedAccountMenuItem, navigateInbox: false);
        }

        private void ReorderAccountMenuItems(Dictionary<Guid, int> newAccountOrder)
        {
            foreach (var item in newAccountOrder)
            {
                if (!MenuItems.TryGetAccountMenuItem(item.Key, out IAccountMenuItem menuItem)) return;

                MenuItems.Move(MenuItems.IndexOf(menuItem), item.Value);
            }
        }

        public void Receive(AccountMenuItemsReordered message) => ReorderAccountMenuItems(message.newOrderDictionary);

        private async void UpdateFolderCollection(IMailItemFolder updatedMailItemFolder)
        {
            var menuItem = MenuItems.GetAllFolderMenuItems(updatedMailItemFolder.Id);

            if (!menuItem.Any()) return;

            foreach (var item in menuItem)
            {
                await ExecuteUIThread(() =>
                {
                    item.UpdateFolder(updatedMailItemFolder);
                });
            }
        }

        protected override void OnFolderRenamed(IMailItemFolder mailItemFolder)
        {
            base.OnFolderRenamed(mailItemFolder);

            UpdateFolderCollection(mailItemFolder);
        }

        protected override void OnFolderSynchronizationEnabled(IMailItemFolder mailItemFolder)
        {
            base.OnFolderSynchronizationEnabled(mailItemFolder);

            UpdateFolderCollection(mailItemFolder);
        }

        public async void Receive(AccountSynchronizationProgressUpdatedMessage message)
        {
            var accountMenuItem = MenuItems.GetSpecificAccountMenuItem(message.AccountId);

            if (accountMenuItem == null) return;

            await ExecuteUIThread(() => { accountMenuItem.SynchronizationProgress = message.Progress; });
        }
    }
}
