using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Messages.Navigation;
using Wino.Core.Requests;

namespace Wino.Mail.ViewModels
{
    public partial class AccountDetailsPageViewModel : BaseViewModel
    {
        private readonly IWinoSynchronizerFactory _synchronizerFactory;
        private readonly IAccountService _accountService;
        private readonly IFolderService _folderService;

        public MailAccount Account { get; set; }
        public ObservableCollection<IMailItemFolder> CurrentFolders { get; set; } = new ObservableCollection<IMailItemFolder>();

        [ObservableProperty]
        private bool isFocusedInboxEnabled;

        [ObservableProperty]
        private bool areNotificationsEnabled;

        [ObservableProperty]
        private bool isAppendMessageSettingVisible;

        [ObservableProperty]
        private bool isAppendMessageSettinEnabled;

        public bool IsFocusedInboxSupportedForAccount => Account != null && Account.Preferences.IsFocusedInboxEnabled != null;


        public AccountDetailsPageViewModel(IDialogService dialogService,
            IWinoSynchronizerFactory synchronizerFactory,
            IAccountService accountService,
            IFolderService folderService) : base(dialogService)
        {
            _synchronizerFactory = synchronizerFactory;
            _accountService = accountService;
            _folderService = folderService;
        }

        [RelayCommand]
        private Task SetupSpecialFolders()
            => DialogService.HandleSystemFolderConfigurationDialogAsync(Account.Id, _folderService);

        [RelayCommand]
        private void EditSignature()
            => Messenger.Send(new BreadcrumbNavigationRequested("Signature", WinoPage.SignatureManagementPage, Account.Id));

        public Task FolderSyncToggledAsync(IMailItemFolder folderStructure, bool isEnabled)
            => _folderService.ChangeFolderSynchronizationStateAsync(folderStructure.Id, isEnabled);

        public Task FolderShowUnreadToggled(IMailItemFolder folderStructure, bool isEnabled)
            => _folderService.ChangeFolderShowUnreadCountStateAsync(folderStructure.Id, isEnabled);

        [RelayCommand]
        private async Task RenameAccount()
        {
            if (Account == null)
                return;

            var updatedAccount = await DialogService.ShowEditAccountDialogAsync(Account);

            if (updatedAccount != null)
            {
                await _accountService.UpdateAccountAsync(updatedAccount);

                ReportUIChange(new AccountUpdatedMessage(updatedAccount));
            }
        }

        [RelayCommand]
        private async Task DeleteAccount()
        {
            if (Account == null)
                return;

            var confirmation = await DialogService.ShowConfirmationDialogAsync(Translator.DialogMessage_DeleteAccountConfirmationTitle,
                                                                               string.Format(Translator.DialogMessage_DeleteAccountConfirmationMessage, Account.Name),
                                                                               Translator.Buttons_Delete);

            if (!confirmation)
                return;

            await _accountService.DeleteAccountAsync(Account);

            _synchronizerFactory.DeleteSynchronizer(Account);

            // TODO: Clear existing requests.
            // _synchronizationWorker.ClearRequests(Account.Id);

            DialogService.InfoBarMessage(Translator.Info_AccountDeletedTitle, string.Format(Translator.Info_AccountDeletedMessage, Account.Name), InfoBarMessageType.Success);

            Messenger.Send(new BackBreadcrumNavigationRequested());
        }

        public override async void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            if (parameters is Guid accountId)
            {
                Account = await _accountService.GetAccountAsync(accountId);

                IsFocusedInboxEnabled = Account.Preferences.IsFocusedInboxEnabled.GetValueOrDefault();
                AreNotificationsEnabled = Account.Preferences.IsNotificationsEnabled;

                IsAppendMessageSettingVisible = Account.ProviderType == MailProviderType.IMAP4;
                IsAppendMessageSettinEnabled = Account.Preferences.ShouldAppendMessagesToSentFolder;

                OnPropertyChanged(nameof(IsFocusedInboxSupportedForAccount));

                var folderStructures = (await _folderService.GetFolderStructureForAccountAsync(Account.Id, true)).Folders;

                foreach (var folder in folderStructures)
                {
                    CurrentFolders.Add(folder);
                }
            }
        }

        protected override async void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            if (e.PropertyName == nameof(IsFocusedInboxEnabled) && IsFocusedInboxSupportedForAccount)
            {
                Account.Preferences.IsFocusedInboxEnabled = IsFocusedInboxEnabled;

                await _accountService.UpdateAccountAsync(Account);
            }
            else if (e.PropertyName == nameof(AreNotificationsEnabled))
            {
                Account.Preferences.IsNotificationsEnabled = AreNotificationsEnabled;

                await _accountService.UpdateAccountAsync(Account);
            }
            else if (e.PropertyName == nameof(IsAppendMessageSettinEnabled))
            {
                Account.Preferences.ShouldAppendMessagesToSentFolder = IsAppendMessageSettinEnabled;

                await _accountService.UpdateAccountAsync(Account);
            }
        }
    }
}
