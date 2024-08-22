using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmailValidation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Server;

namespace Wino.Mail.ViewModels
{
    public partial class AliasManagementPageViewModel : BaseViewModel
    {
        private readonly IAccountService _accountService;
        private readonly IWinoServerConnectionManager _winoServerConnectionManager;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanSynchronizeAliases))]
        private MailAccount account;

        [ObservableProperty]
        private List<MailAccountAlias> accountAliases = [];

        public bool CanSynchronizeAliases => Account?.IsAliasSyncSupported ?? false;

        public AliasManagementPageViewModel(IDialogService dialogService,
                                            IAccountService accountService,
                                            IWinoServerConnectionManager winoServerConnectionManager) : base(dialogService)
        {
            _accountService = accountService;
            _winoServerConnectionManager = winoServerConnectionManager;
        }

        public override async void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            if (parameters is Guid accountId)
                Account = await _accountService.GetAccountAsync(accountId);

            if (Account == null) return;

            await LoadAliasesAsync();
        }

        private async Task LoadAliasesAsync()
        {
            AccountAliases = await _accountService.GetAccountAliasesAsync(Account.Id);
        }

        [RelayCommand]
        private async Task SetAliasPrimaryAsync(MailAccountAlias alias)
        {
            if (alias.IsPrimary) return;

            AccountAliases.ForEach(a =>
            {
                a.IsPrimary = a == alias;
            });

            await _accountService.UpdateAccountAliasesAsync(Account.Id, AccountAliases);
            await LoadAliasesAsync();
        }

        [RelayCommand]
        private async Task SyncAliasesAsync()
        {
            if (!CanSynchronizeAliases) return;

            var aliasSyncOptions = new SynchronizationOptions()
            {
                AccountId = Account.Id,
                Type = SynchronizationType.Alias
            };

            var aliasSyncResponse = await _winoServerConnectionManager.GetResponseAsync<SynchronizationResult, NewSynchronizationRequested>(new NewSynchronizationRequested(aliasSyncOptions, SynchronizationSource.Client));

            if (aliasSyncResponse.IsSuccess)
                await LoadAliasesAsync();
            else
                DialogService.InfoBarMessage(Translator.GeneralTitle_Error, aliasSyncResponse.Message, InfoBarMessageType.Error);
        }

        [RelayCommand]
        private async Task AddNewAliasAsync()
        {
            var createdAliasDialog = await DialogService.ShowCreateAccountAliasDialogAsync();

            if (createdAliasDialog.CreatedAccountAlias == null) return;

            var newAlias = createdAliasDialog.CreatedAccountAlias;

            // Check existence.
            if (AccountAliases.Any(a => a.AliasAddress == newAlias.AliasAddress))
            {
                await DialogService.ShowMessageAsync(Translator.DialogMessage_AliasExistsTitle,
                                                     Translator.DialogMessage_AliasExistsMessage,
                                                     WinoCustomMessageDialogIcon.Warning);
                return;
            }

            // Validate all addresses.
            if (!EmailValidator.Validate(newAlias.AliasAddress) || (!string.IsNullOrEmpty(newAlias.ReplyToAddress) && !EmailValidator.Validate(newAlias.ReplyToAddress)))
            {
                await DialogService.ShowMessageAsync(Translator.DialogMessage_InvalidAliasMessage,
                                                     Translator.DialogMessage_InvalidAliasTitle,
                                                     WinoCustomMessageDialogIcon.Warning);
                return;
            }

            newAlias.AccountId = Account.Id;

            AccountAliases.Add(newAlias);

            await _accountService.UpdateAccountAliasesAsync(Account.Id, AccountAliases);
            DialogService.InfoBarMessage(Translator.DialogMessage_AliasCreatedTitle, Translator.DialogMessage_AliasCreatedMessage, InfoBarMessageType.Success);

            await LoadAliasesAsync();
        }

        [RelayCommand]
        private async Task DeleteAliasAsync(MailAccountAlias alias)
        {
            // Primary aliases can't be deleted.
            if (alias.IsPrimary)
            {
                await DialogService.ShowMessageAsync(Translator.Info_CantDeletePrimaryAliasMessage,
                                                     Translator.GeneralTitle_Warning,
                                                     WinoCustomMessageDialogIcon.Warning);
                return;
            }

            // Root aliases can't be deleted.
            if (alias.IsRootAlias)
            {
                await DialogService.ShowMessageAsync(Translator.DialogMessage_CantDeleteRootAliasTitle,
                                                     Translator.DialogMessage_CantDeleteRootAliasMessage,
                                                     WinoCustomMessageDialogIcon.Warning);
                return;
            }

            await _accountService.DeleteAccountAliasAsync(alias.Id);
            await LoadAliasesAsync();
        }
    }
}
