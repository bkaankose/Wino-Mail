using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.AppCenter.Crashes;
using MoreLinq;
using MoreLinq.Extensions;
using Serilog;
using Wino.Core;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Extensions;
using Wino.Core.MenuItems;
using Wino.Core.Messages.Accounts;
using Wino.Core.Messages.Mails;
using Wino.Core.Messages.Navigation;
using Wino.Core.Messages.Shell;
using Wino.Core.Messages.Synchronization;
using Wino.Core.Requests;
using Wino.Core.Services;

namespace Wino.Mail.ViewModels
{
    public partial class AppShellViewModel : BaseViewModel,
        ISynchronizationProgress,
        IRecipient<NewSynchronizationRequested>,
        IRecipient<NavigateSettingsRequested>,
        IRecipient<MailtoProtocolMessageRequested>,
        IRecipient<RefreshUnreadCountsMessage>,
        IRecipient<AccountsMenuRefreshRequested>,
        IRecipient<MergedInboxRenamed>,
        IRecipient<LanguageChanged>
    {
        #region Menu Items

        [ObservableProperty]
        private object selectedMenuItem;

        private IAccountMenuItem latestSelectedAccountMenuItem;

        public MenuItemCollection FooterItems { get; set; } = [];
        public MenuItemCollection MenuItems { get; set; } = [];

        private readonly SettingsItem SettingsItem = new SettingsItem();

        private readonly RateMenuItem RatingItem = new RateMenuItem();

        private readonly ManageAccountsMenuItem ManageAccountsMenuItem = new ManageAccountsMenuItem();

        public NewMailMenuItem CreateMailMenuItem = new NewMailMenuItem();

        #endregion

        public IStatePersistanceService StatePersistenceService { get; }
        public IPreferencesService PreferencesService { get; }
        public IWinoNavigationService NavigationService { get; }

        private readonly IFolderService _folderService;
        private readonly IAccountService _accountService;
        private readonly IContextMenuItemService _contextMenuItemService;
        private readonly IStoreRatingService _storeRatingService;
        private readonly ILaunchProtocolService _launchProtocolService;
        private readonly INotificationBuilder _notificationBuilder;
        private readonly IWinoRequestDelegator _winoRequestDelegator;

        private readonly IWinoSynchronizerFactory _synchronizerFactory;
        private readonly IBackgroundTaskService _backgroundTaskService;
        private readonly IMimeFileService _mimeFileService;

        private readonly INativeAppService _nativeAppService;
        private readonly IMailService _mailService;

        private readonly SemaphoreSlim accountInitFolderUpdateSlim = new SemaphoreSlim(1);

        public AppShellViewModel(IDialogService dialogService,
                                 IWinoNavigationService navigationService,
                                 IWinoSynchronizerFactory synchronizerFactory,
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
                                 IStatePersistanceService statePersistanceService) : base(dialogService)
        {
            StatePersistenceService = statePersistanceService;
            PreferencesService = preferencesService;
            NavigationService = navigationService;

            _synchronizerFactory = synchronizerFactory;
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
                FooterItems.Add(RatingItem);
                FooterItems.Add(SettingsItem);
            });
        }

        private async Task LoadAccountsAsync()
        {
            var accounts = await _accountService.GetAccountsAsync();

            // Group accounts by merged account.
            var groupedAccounts = accounts.GroupBy(a => a.MergedInboxId);

            foreach (var accountGroup in groupedAccounts)
            {
                var mergedInbox = accountGroup.Key;

                if (mergedInbox == null)
                {
                    // This account is not merged. Create menu item for each account.
                    foreach (var account in accountGroup)
                    {
                        await CreateNestedAccountMenuItem(account);
                    }
                }
                else
                {
                    // Accounts are merged. Create menu item for merged inbox.
                    await CreateMergedInboxMenuItemAsync(accountGroup);
                }
            }

            // Re-assign latest selected account menu item for containers to reflect changes better.
            // Also , this will ensure that the latest selected account is still selected after re-creation.

            if (latestSelectedAccountMenuItem != null)
            {
                latestSelectedAccountMenuItem = MenuItems.GetAccountMenuItem(latestSelectedAccountMenuItem.EntityId.GetValueOrDefault());

                if (latestSelectedAccountMenuItem != null)
                {
                    latestSelectedAccountMenuItem.IsSelected = true;
                }
            }
        }

        protected override async void OnFolderUpdated(MailItemFolder updatedFolder, MailAccount account)
        {
            base.OnFolderUpdated(updatedFolder, account);

            if (updatedFolder == null) return;

            var folderMenuItemsToUpdate = MenuItems.GetFolderItems(updatedFolder.Id);

            foreach (var item in folderMenuItemsToUpdate)
            {
                await ExecuteUIThread(() =>
                {
                    item.UpdateFolder(updatedFolder);
                });
            }
        }

        private async Task CreateMergedInboxMenuItemAsync(IEnumerable<MailAccount> accounts)
        {
            var mergedInbox = accounts.First().MergedInbox;
            var mergedInboxMenuItem = new MergedAccountMenuItem(mergedInbox, null); // Merged accounts are parentless.

            // Store common special type folders.
            var commonFolderList = new Dictionary<MailAccount, IMailItemFolder>();

            // Map special folder types for each account.
            var accountTreeList = new List<AccountFolderTree>();

            foreach (var account in accounts)
            {
                var accountStructure = await _folderService.GetFolderStructureForAccountAsync(account.Id, includeHiddenFolders: true);
                accountTreeList.Add(accountStructure);
            }

            var allFolders = accountTreeList.SelectMany(a => a.Folders);

            // 1. Group sticky folders by special folder type.
            // 2. Merge all folders that are sticky and have the same special folder type.
            // 3. Add merged folder menu items to the merged inbox menu item.
            // 4. Add remaining sticky folders that doesn't exist in all accounts as plain folder menu items.

            var stickyFolders = allFolders.Where(a => a.IsSticky);

            var grouped = stickyFolders
                          .GroupBy(a => a.SpecialFolderType)
                          .Where(a => accountTreeList.All(b => b.HasSpecialTypeFolder(a.Key)));

            var mergedInboxItems = grouped.Select(a => new MergedAccountFolderMenuItem(a.ToList(), mergedInboxMenuItem, mergedInbox));

            // Shared common folders.
            foreach (var mergedInboxFolder in mergedInboxItems)
            {
                mergedInboxMenuItem.SubMenuItems.Add(mergedInboxFolder);
            }

            var usedFolderIds = mergedInboxItems.SelectMany(a => a.Parameter.Select(a => a.Id));
            var remainingStickyFolders = stickyFolders.Where(a => !usedFolderIds.Contains(a.Id));

            // Marked as sticky, but doesn't exist in all accounts. Add as plain folder menu item.
            foreach (var remainingStickyFolder in remainingStickyFolders)
            {
                var account = accounts.FirstOrDefault(a => a.Id == remainingStickyFolder.MailAccountId);
                mergedInboxMenuItem.SubMenuItems.Add(new FolderMenuItem(remainingStickyFolder, account, mergedInboxMenuItem));
            }


            var mergedMoreItem = new MergedAccountMoreFolderMenuItem(null, null, mergedInboxMenuItem);

            // 2. Sticky folder preparation is done. Continue with regular account menu items.

            foreach (var accountTree in accountTreeList)
            {
                var tree = accountTree.GetAccountMenuTree(mergedInboxMenuItem);

                mergedMoreItem.SubMenuItems.Add(tree);
            }

            mergedInboxMenuItem.SubMenuItems.Add(mergedMoreItem);

            MenuItems.Add(mergedInboxMenuItem);

            // Instead of refreshing all accounts, refresh the merged account only.
            // Receiver will handle it.

            Messenger.Send(new RefreshUnreadCountsMessage(mergedInbox.Id));
        }

        private async Task<IAccountMenuItem> CreateNestedAccountMenuItem(MailAccount account)
        {
            try
            {
                await accountInitFolderUpdateSlim.WaitAsync();

                // Don't remove but replace existing record.
                int existingIndex = -1;

                var existingAccountMenuItem = MenuItems.FirstOrDefault(a => a is AccountMenuItem accountMenuItem && accountMenuItem.Parameter.Id == account.Id);

                if (existingAccountMenuItem != null)
                {
                    existingIndex = MenuItems.IndexOf(existingAccountMenuItem);
                }

                // Create account structure with integrator for this menu item.
                var accountStructure = await _folderService.GetFolderStructureForAccountAsync(account.Id, includeHiddenFolders: false);

                var createdMenuItem = accountStructure.GetAccountMenuTree();

                await ExecuteUIThread(() =>
                {
                    if (existingIndex >= 0)
                    {
                        createdMenuItem.IsExpanded = existingAccountMenuItem.IsExpanded;

                        MenuItems.RemoveAt(existingIndex);
                        MenuItems.Insert(existingIndex, createdMenuItem);
                    }
                    else
                    {
                        MenuItems.AddAccountMenuItem(createdMenuItem);
                    }
                });

                Messenger.Send(new RefreshUnreadCountsMessage(account.Id));

                return createdMenuItem;
            }
            catch (Exception ex)
            {
                Log.Error(ex, WinoErrors.AccountStructureRender);
            }
            finally
            {
                accountInitFolderUpdateSlim.Release();
            }

            return null;
        }

        public override async void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            await CreateFooterItemsAsync();

            await RecreateMenuItemsAsync();
            await ProcessLaunchOptionsAsync();

#if !DEBUG
            await ForceAllAccountSynchronizationsAsync();
#endif
            await ConfigureBackgroundTasksAsync();
        }

        private async Task ConfigureBackgroundTasksAsync()
        {
            try
            {
                await _backgroundTaskService.HandleBackgroundTaskRegistrations();
            }
            catch (BackgroundTaskExecutionRequestDeniedException)
            {
                await DialogService.ShowMessageAsync(Translator.Info_BackgroundExecutionDeniedMessage, Translator.Info_BackgroundExecutionDeniedTitle);
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
                    Type = SynchronizationType.Inbox
                };


                Messenger.Send(new NewSynchronizationRequested(options));
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

                        if (account != null && MenuItems.GetAccountMenuItem(account.Id) is IAccountMenuItem accountMenuItem)
                        {
                            ChangeLoadedAccount(accountMenuItem);

                            WeakReferenceMessenger.Default.Send(accountExtendedMessage);

                            _launchProtocolService.LaunchParameter = null;
                        }
                        else
                        {
                            ProcessLaunchDefault();
                        }
                    }
                }
                else
                {
                    bool hasMailtoActivation = _launchProtocolService.MailtoParameters != null;

                    if (hasMailtoActivation)
                    {
                        // mailto activation. Create new mail with specific delivered address as receiver.

                        WeakReferenceMessenger.Default.Send(new MailtoProtocolMessageRequested());
                    }
                    else
                    {
                        // Use default startup extending.
                        ProcessLaunchDefault();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, WinoErrors.StartupAccountExtendFail);
            }
        }

        private void ProcessLaunchDefault()
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
                        ChangeLoadedAccount(startupAccountMenuItem);
                    }
                }
            }
        }

        public async Task NavigateFolderAsync(IBaseFolderMenuItem baseFolderMenuItem)
        {
            // It's already there. Don't navigate again.
            if (SelectedMenuItem == baseFolderMenuItem) return;

            SelectedMenuItem = baseFolderMenuItem;
            baseFolderMenuItem.IsSelected = true;

            var mailInitCompletionSource = new TaskCompletionSource<bool>();
            var args = new NavigateMailFolderEventArgs(baseFolderMenuItem, mailInitCompletionSource);

            NavigationService.NavigateFolder(args);
            StatePersistenceService.CoreWindowTitle = $"{baseFolderMenuItem.AssignedAccountName} - {baseFolderMenuItem.FolderName}";

            // Wait until mail list page picks up the event and finish initialization of the mails.
            await mailInitCompletionSource.Task;
        }

        private async Task NavigateSpecialFolderAsync(MailAccount account, SpecialFolderType specialFolderType, bool extendAccountMenu)
        {
            try
            {
                if (account == null) return;

                // If the account is inside a merged account, expand the merged account and navigate to shared folder.
                if (MenuItems.TryGetMergedAccountRootFolderMenuItemByAccountId(account.Id, specialFolderType, out MergedAccountFolderMenuItem mergedFolderItem))
                {
                    mergedFolderItem.Expand();
                    await NavigateFolderAsync(mergedFolderItem);
                }
                else if (MenuItems.TryGetRootSpecialFolderMenuItem(account.Id, specialFolderType, out FolderMenuItem rootFolderMenuItem))
                {
                    // Account is not in merged account. Navigate to root folder.

                    rootFolderMenuItem.Expand();
                    await NavigateFolderAsync(rootFolderMenuItem);
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
                await _winoRequestDelegator.ExecuteAsync(operation, folder);
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
                ChangeLoadedAccount(clickedAccountMenuItem);
            }
        }

        private async void ChangeLoadedAccount(IAccountMenuItem clickedBaseAccountMenuItem, bool navigateInbox = true)
        {
            if (clickedBaseAccountMenuItem == null) return;

            // User clicked an account in Windows Mail style menu.
            // List folders for this account and select Inbox.

            await ExecuteUIThread(() =>
            {
                if (latestSelectedAccountMenuItem != null)
                {
                    latestSelectedAccountMenuItem.IsSelected = false;
                }

                clickedBaseAccountMenuItem.IsSelected = true;
                

                latestSelectedAccountMenuItem = clickedBaseAccountMenuItem;


                if (clickedBaseAccountMenuItem is AccountMenuItem accountMenuItem)
                {
                    MenuItems.ReplaceFolders(accountMenuItem.SubMenuItems);
                }
                else if (clickedBaseAccountMenuItem is MergedAccountMenuItem mergedAccountMenuItem)
                {
                    MenuItems.ReplaceFolders(mergedAccountMenuItem.SubMenuItems);
                }
            });

            if (navigateInbox)
            {
                await Task.Yield();

                await ExecuteUIThread(() =>
                {
                    NavigateInbox(clickedBaseAccountMenuItem);
                });
            }
        }

        private async void NavigateInbox(IAccountMenuItem clickedBaseAccountMenuItem)
        {
            if (clickedBaseAccountMenuItem is AccountMenuItem accountMenuItem)
            {
                if (MenuItems.TryGetWindowsStyleRootSpecialFolderMenuItem(accountMenuItem.AccountId, SpecialFolderType.Inbox, out FolderMenuItem inboxFolder))
                {
                    await NavigateFolderAsync(inboxFolder);
                }
            }
            else if (clickedBaseAccountMenuItem is MergedAccountMenuItem mergedAccountMenuItem)
            {
                if (MenuItems.TryGetMergedAccountSpecialFolderMenuItem(mergedAccountMenuItem.EntityId.GetValueOrDefault(), SpecialFolderType.Inbox, out IBaseFolderMenuItem inboxFolder))
                {
                    await NavigateFolderAsync(inboxFolder);
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
                    Messenger.Send(new CreateNewMailWithMultipleAccountsRequested(accounts));
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

                // Include mail to parameters for parsing mailto if any.
                MailtoParameters = _launchProtocolService.MailtoParameters
            };

            var createdMimeMessage = await _mailService.CreateDraftMimeMessageAsync(account.Id, draftOptions).ConfigureAwait(false);
            var createdDraftMailMessage = await _mailService.CreateDraftAsync(account, createdMimeMessage).ConfigureAwait(false);

            var draftPreperationRequest = new DraftPreperationRequest(account, createdDraftMailMessage, createdMimeMessage);
            await _winoRequestDelegator.ExecuteAsync(draftPreperationRequest);
        }



        public async void Receive(NewSynchronizationRequested message)
        {
            // Don't send message for sync completion when we execute requests.
            // People are usually interested in seeing the notification after they trigger the synchronization.

            bool shouldReportSynchronizationResult = message.Options.Type != SynchronizationType.ExecuteRequests;

            var synchronizer = _synchronizerFactory.GetAccountSynchronizer(message.Options.AccountId);

            if (synchronizer == null) return;

            var accountId = message.Options.AccountId;

            message.Options.ProgressListener = this;

            bool isSynchronizationSucceeded = false;

            try
            {
                // TODO: Cancellation Token
                var synchronizationResult = await synchronizer.SynchronizeAsync(message.Options);

                isSynchronizationSucceeded = synchronizationResult.CompletedState == SynchronizationCompletedState.Success;

                // Create notification for synchronization result.
                if (synchronizationResult.DownloadedMessages.Any())
                {
                    var accountInboxFolder = await _folderService.GetSpecialFolderByAccountIdAsync(message.Options.AccountId, SpecialFolderType.Inbox);

                    if (accountInboxFolder == null) return;

                    await _notificationBuilder.CreateNotificationsAsync(accountInboxFolder.Id, synchronizationResult.DownloadedMessages);
                }
            }
            catch (AuthenticationAttentionException)
            {
                await SetAccountAttentionAsync(accountId, AccountAttentionReason.InvalidCredentials);
            }
            catch (SystemFolderConfigurationMissingException)
            {
                await SetAccountAttentionAsync(accountId, AccountAttentionReason.MissingSystemFolderConfiguration);
            }
            catch (OperationCanceledException)
            {
                DialogService.InfoBarMessage(Translator.Info_SyncCanceledMessage, Translator.Info_SyncCanceledMessage, InfoBarMessageType.Warning);
            }
            catch (Exception ex)
            {
                DialogService.InfoBarMessage(Translator.Info_SyncFailedTitle, ex.Message, InfoBarMessageType.Error);
            }
            finally
            {
                if (shouldReportSynchronizationResult)
                    Messenger.Send(new AccountSynchronizationCompleted(accountId,
                                                                       isSynchronizationSucceeded ? SynchronizationCompletedState.Success : SynchronizationCompletedState.Failed,
                                                                       message.Options.GroupedSynchronizationTrackingId));
            }
        }


        protected override async void OnAccountUpdated(MailAccount updatedAccount)
            => await ExecuteUIThread(() => { MenuItems.GetAccountMenuItem(updatedAccount.Id)?.UpdateAccount(updatedAccount); });

        protected override void OnAccountRemoved(MailAccount removedAccount)
            => Messenger.Send(new AccountsMenuRefreshRequested(true));

        protected override async void OnAccountCreated(MailAccount createdAccount)
        {
            var createdMenuItem = await CreateNestedAccountMenuItem(createdAccount);

            if (createdMenuItem == null) return;

            ChangeLoadedAccount(createdMenuItem);

            // Each created account should start a new synchronization automatically.
            var options = new SynchronizationOptions()
            {
                AccountId = createdAccount.Id,
                Type = SynchronizationType.Full,
            };

            Messenger.Send(new NewSynchronizationRequested(options));

            await _nativeAppService.PinAppToTaskbarAsync();
        }

        /// <summary>
        /// Updates given single account menu item's unread count for all folders.
        /// </summary>
        /// <param name="accountMenuItem">Menu item to update unread count for.</param>
        /// <returns>Unread item count for Inbox only.</returns>
        private async Task<int> UpdateSingleAccountMenuItemUnreadCountAsync(AccountMenuItem accountMenuItem)
        {
            var accountId = accountMenuItem.AccountId;
            int inboxItemCount = 0;

            // Get the folders needed to be refreshed.
            var allFolders = await _folderService.GetUnreadUpdateFoldersAsync(accountId);

            foreach (var folder in allFolders)
            {
                var unreadItemCount = await UpdateAccountFolderUnreadItemCountAsync(accountMenuItem, folder.Id);

                if (folder.SpecialFolderType == SpecialFolderType.Inbox)
                {
                    inboxItemCount = unreadItemCount;

                    await ExecuteUIThread(() => { accountMenuItem.UnreadItemCount = unreadItemCount; });
                }
            }

            return inboxItemCount;
        }

        private async Task RefreshUnreadCountsForAccountAsync(Guid accountId)
        {
            // TODO: Merged accounts unread item count.

            var accountMenuItem = MenuItems.GetAccountMenuItem(accountId);

            if (accountMenuItem == null) return;

            if (accountMenuItem is AccountMenuItem singleAccountMenuItem)
            {
                await UpdateSingleAccountMenuItemUnreadCountAsync(singleAccountMenuItem);

            }
            else if (accountMenuItem is MergedAccountMenuItem mergedAccountMenuItem)
            {
                // Merged account.
                // Root account should include all parent accounts' unread item count.

                int totalUnreadCount = 0;

                var individualAccountMenuItems = mergedAccountMenuItem.GetAccountMenuItems();

                foreach (var singleMenuItem in individualAccountMenuItems)
                {
                    totalUnreadCount += await UpdateSingleAccountMenuItemUnreadCountAsync(singleMenuItem);
                }

                // At this point all single accounts are calculated.
                // Merge account folder's menu items can be calculated from those values for precision.

                await ExecuteUIThread(() =>
                {
                    mergedAccountMenuItem.RefreshFolderItemCount();
                    mergedAccountMenuItem.UnreadItemCount = totalUnreadCount;
                });
            }

            await ExecuteUIThread(async () => { await _notificationBuilder.UpdateTaskbarIconBadgeAsync(); });
        }

        private async Task<int> UpdateAccountFolderUnreadItemCountAsync(AccountMenuItem accountMenuItem, Guid folderId)
        {
            if (accountMenuItem == null) return 0;

            var folder = accountMenuItem.FlattenedFolderHierarchy.Find(a => a.Parameter?.Id == folderId);

            if (folder == null) return 0;

            int folderUnreadItemCount = 0;

            folderUnreadItemCount = await _folderService.GetFolderNotificationBadgeAsync(folder.Parameter.Id).ConfigureAwait(false);

            await ExecuteUIThread(() => { folder.UnreadItemCount = folderUnreadItemCount; });

            return folderUnreadItemCount;
        }

        private async Task SetAccountAttentionAsync(Guid accountId, AccountAttentionReason reason)
        {
            var accountMenuItem = MenuItems.GetAccountMenuItem(accountId);

            if (accountMenuItem == null) return;

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

        public async void AccountProgressUpdated(Guid accountId, int progress)
        {
            var accountMenuItem = MenuItems.GetSpecificAccountMenuItem(accountId);

            if (accountMenuItem == null) return;

            await ExecuteUIThread(() => { accountMenuItem.SynchronizationProgress = progress; });
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
            => await RefreshUnreadCountsForAccountAsync(message.AccountId);

        public async void Receive(AccountsMenuRefreshRequested message)
        {
            await RecreateMenuItemsAsync();

            if (message.AutomaticallyNavigateFirstItem)
            {
                if (MenuItems.FirstOrDefault(a => a is IAccountMenuItem) is IAccountMenuItem firstAccount)
                {
                    ChangeLoadedAccount(firstAccount);
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

            ChangeLoadedAccount(latestSelectedAccountMenuItem, navigateInbox: false);
        }
    }
}
