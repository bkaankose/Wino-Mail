using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels.Data;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels
{
    public partial class MergedAccountDetailsPageViewModel : MailBaseViewModel,
        IRecipient<MergedInboxRenamed>
    {
        [ObservableProperty]
        private MergedAccountProviderDetailViewModel editingMergedAccount;

        [ObservableProperty]
        private string mergedAccountName;

        public ObservableCollection<AccountProviderDetailViewModel> LinkedAccounts { get; set; } = [];
        public ObservableCollection<AccountProviderDetailViewModel> UnlinkedAccounts { get; set; } = [];

        // Empty Guid is passed for new created merged inboxes.
        public bool IsMergedInboxSaved => EditingMergedAccount != null && EditingMergedAccount.MergedInbox.Id != Guid.Empty;

        public bool CanUnlink => IsMergedInboxSaved;

        // There must be at least 2 accounts linked to a merged account for link to exist.
        public bool ShouldDeleteMergedAccount => LinkedAccounts.Count < 2;

        public bool CanSaveChanges
        {
            get
            {
                if (IsMergedInboxSaved)
                {
                    return ShouldDeleteMergedAccount || IsEditingAccountsDirty();
                }
                else
                {
                    return LinkedAccounts.Any();
                }
            }
        }

        private readonly IMailDialogService _dialogService;
        private readonly IAccountService _accountService;
        private readonly IPreferencesService _preferencesService;
        private readonly IProviderService _providerService;

        public MergedAccountDetailsPageViewModel(IMailDialogService dialogService,
                                                 IAccountService accountService,
                                                 IPreferencesService preferencesService,
                                                 IProviderService providerService)
        {
            _dialogService = dialogService;
            _accountService = accountService;
            _preferencesService = preferencesService;
            _providerService = providerService;
        }

        [RelayCommand(CanExecute = nameof(CanUnlink))]
        private async Task UnlinkAccountsAsync()
        {
            if (EditingMergedAccount == null) return;

            var isConfirmed = await _dialogService.ShowConfirmationDialogAsync(Translator.DialogMessage_UnlinkAccountsConfirmationMessage, Translator.DialogMessage_UnlinkAccountsConfirmationTitle, Translator.Buttons_Yes);

            if (!isConfirmed) return;

            await _accountService.UnlinkMergedInboxAsync(EditingMergedAccount.MergedInbox.Id);

            Messenger.Send(new BackBreadcrumNavigationRequested());
        }

        [RelayCommand(CanExecute = nameof(CanSaveChanges))]
        private async Task SaveChangesAsync()
        {
            if (ShouldDeleteMergedAccount)
            {
                await UnlinkAccountsAsync();
            }
            else
            {
                if (IsMergedInboxSaved)
                {
                    await _accountService.UpdateMergedInboxAsync(EditingMergedAccount.MergedInbox.Id, LinkedAccounts.Select(a => a.Account.Id).ToList());
                }
                else
                {
                    await _accountService.CreateMergeAccountsAsync(EditingMergedAccount.MergedInbox, LinkedAccounts.Select(a => a.Account).ToList());
                }

                // Startup entity is linked now. Change the startup entity.
                if (_preferencesService.StartupEntityId != null && LinkedAccounts.Any(a => a.StartupEntityId == _preferencesService.StartupEntityId))
                {
                    _preferencesService.StartupEntityId = EditingMergedAccount.MergedInbox.Id;
                }
            }

            Messenger.Send(new BackBreadcrumNavigationRequested());
        }

        [RelayCommand]
        private async Task RenameLinkAsync()
        {
            if (EditingMergedAccount == null) return;

            var newName = await _dialogService.ShowTextInputDialogAsync(EditingMergedAccount.MergedInbox.Name,
                                                                       Translator.DialogMessage_RenameLinkedAccountsTitle,
                                                                       Translator.DialogMessage_RenameLinkedAccountsMessage,
                                                                       Translator.FolderOperation_Rename);

            if (string.IsNullOrWhiteSpace(newName)) return;

            EditingMergedAccount.MergedInbox.Name = newName;

            // Update database record as well.
            if (IsMergedInboxSaved)
            {
                await _accountService.RenameMergedAccountAsync(EditingMergedAccount.MergedInbox.Id, newName);
            }
            else
            {
                // Publish the message manually since the merged inbox is not saved yet.
                // This is only for breadcrump item update.

                Messenger.Send(new MergedInboxRenamed(EditingMergedAccount.MergedInbox.Id, newName));
            }
        }

        [RelayCommand]
        private void LinkAccount(AccountProviderDetailViewModel account)
        {
            LinkedAccounts.Add(account);
            UnlinkedAccounts.Remove(account);
        }

        [RelayCommand]
        private void UnlinkAccount(AccountProviderDetailViewModel account)
        {
            UnlinkedAccounts.Add(account);
            LinkedAccounts.Remove(account);
        }

        private bool IsEditingAccountsDirty()
        {
            if (EditingMergedAccount == null) return false;

            return EditingMergedAccount.HoldingAccounts.Count != LinkedAccounts.Count ||
                   EditingMergedAccount.HoldingAccounts.Any(a => !LinkedAccounts.Any(la => la.Account.Id == a.Account.Id));
        }

        public override void OnNavigatedFrom(NavigationMode mode, object parameters)
        {
            base.OnNavigatedFrom(mode, parameters);

            LinkedAccounts.CollectionChanged -= LinkedAccountsUpdated;
        }

        private void LinkedAccountsUpdated(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(ShouldDeleteMergedAccount));
            SaveChangesCommand.NotifyCanExecuteChanged();

            // TODO: Preview common folders for all linked accounts.
            // Basically showing a preview of how menu items will look.
        }

        public override async void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            LinkedAccounts.CollectionChanged -= LinkedAccountsUpdated;
            LinkedAccounts.CollectionChanged += LinkedAccountsUpdated;

            if (parameters is MergedAccountProviderDetailViewModel editingMergedAccount)
            {
                MergedAccountName = editingMergedAccount.MergedInbox.Name;
                EditingMergedAccount = editingMergedAccount;

                foreach (var account in editingMergedAccount.HoldingAccounts)
                {
                    LinkedAccounts.Add(account);
                }

                // Load unlinked accounts.

                var allAccounts = await _accountService.GetAccountsAsync();

                foreach (var account in allAccounts)
                {
                    if (!LinkedAccounts.Any(a => a.Account.Id == account.Id))
                    {
                        var provider = _providerService.GetProviderDetail(account.ProviderType);

                        UnlinkedAccounts.Add(new AccountProviderDetailViewModel(provider, account));
                    }
                }
            }

            UnlinkAccountsCommand.NotifyCanExecuteChanged();
        }

        public void Receive(MergedInboxRenamed message)
        {
            if (EditingMergedAccount?.MergedInbox.Id == message.MergedInboxId)
            {
                EditingMergedAccount.MergedInbox.Name = message.NewName;
            }
        }
    }
}
