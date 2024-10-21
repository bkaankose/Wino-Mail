using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoreLinq;
using MoreLinq.Extensions;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Mail.ViewModels
{
    public partial class SignatureManagementPageViewModel(IMailDialogService dialogService,
                                            ISignatureService signatureService,
                                            IAccountService accountService) : MailBaseViewModel
    {
        public ObservableCollection<AccountSignature> Signatures { get; set; } = [];

        [ObservableProperty]
        private bool isSignatureEnabled;

        private int signatureForNewMessagesIndex;

        public Guid EmptyGuid { get; } = Guid.Empty;

        public int SignatureForNewMessagesIndex
        {
            get => signatureForNewMessagesIndex;
            set
            {
                if (value == -1)
                {
                    SetProperty(ref signatureForNewMessagesIndex, 0);
                }
                else
                {
                    SetProperty(ref signatureForNewMessagesIndex, value);
                }
            }
        }

        private int signatureForFollowingMessagesIndex;

        public int SignatureForFollowingMessagesIndex
        {
            get => signatureForFollowingMessagesIndex;
            set
            {
                if (value == -1)
                {
                    SetProperty(ref signatureForFollowingMessagesIndex, 0);
                }
                else
                {
                    SetProperty(ref signatureForFollowingMessagesIndex, value);
                }
            }
        }

        private MailAccount Account { get; set; }

        private readonly IMailDialogService _dialogService = dialogService;
        private readonly ISignatureService _signatureService = signatureService;
        private readonly IAccountService _accountService = accountService;

        public override async void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            if (parameters is Guid accountId)
                Account = await _accountService.GetAccountAsync(accountId);

            if (Account == null) return;

            var dbSignatures = await _signatureService.GetSignaturesAsync(Account.Id);
            IsSignatureEnabled = Account.Preferences.IsSignatureEnabled;

            Signatures.Clear();
            Signatures.Add(new AccountSignature { Id = EmptyGuid, Name = Translator.SettingsSignature_NoneSignatureName });
            dbSignatures.ForEach(Signatures.Add);

            SignatureForNewMessagesIndex = Signatures.IndexOf(Signatures.FirstOrDefault(x => x.Id == Account.Preferences.SignatureIdForNewMessages));
            SignatureForFollowingMessagesIndex = Signatures.IndexOf(Signatures.FirstOrDefault(x => x.Id == Account.Preferences.SignatureIdForFollowingMessages));
        }

        protected override async void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            switch (e.PropertyName)
            {
                case nameof(IsSignatureEnabled):
                    Account.Preferences.IsSignatureEnabled = IsSignatureEnabled;
                    await _accountService.UpdateAccountAsync(Account);
                    break;
                case nameof(SignatureForNewMessagesIndex):
                    Account.Preferences.SignatureIdForNewMessages = SignatureForNewMessagesIndex > -1
                        && Signatures[SignatureForNewMessagesIndex].Id != EmptyGuid
                        ? Signatures[SignatureForNewMessagesIndex].Id : null;
                    await _accountService.UpdateAccountAsync(Account);
                    break;
                case nameof(SignatureForFollowingMessagesIndex):
                    Account.Preferences.SignatureIdForFollowingMessages = SignatureForFollowingMessagesIndex > -1
                        && Signatures[SignatureForFollowingMessagesIndex].Id != EmptyGuid
                        ? Signatures[SignatureForFollowingMessagesIndex].Id : null;
                    await _accountService.UpdateAccountAsync(Account);
                    break;
            }
        }

        [RelayCommand]
        private async Task OpenSignatureEditorCreateAsync()
        {
            var dialogResult = await _dialogService.ShowSignatureEditorDialog();

            if (dialogResult == null) return;

            dialogResult.MailAccountId = Account.Id;
            Signatures.Add(dialogResult);
            await _signatureService.CreateSignatureAsync(dialogResult);
        }

        [RelayCommand]
        private async Task OpenSignatureEditorEditAsync(AccountSignature signatureModel)
        {
            var dialogResult = await _dialogService.ShowSignatureEditorDialog(signatureModel);

            if (dialogResult == null) return;

            var indexOfCurrentSignature = Signatures.IndexOf(signatureModel);
            var signatureNewMessagesIndex = SignatureForNewMessagesIndex;
            var signatureFollowingMessagesIndex = SignatureForFollowingMessagesIndex;

            Signatures[indexOfCurrentSignature] = dialogResult;

            // Reset selection to point updated signature.
            // When Item updated/removed index switches to -1. We save index that was used before and update -1 to it.
            if (signatureNewMessagesIndex == indexOfCurrentSignature)
                SignatureForNewMessagesIndex = indexOfCurrentSignature;

            if (signatureFollowingMessagesIndex == indexOfCurrentSignature)
                SignatureForFollowingMessagesIndex = indexOfCurrentSignature;

            await _signatureService.UpdateSignatureAsync(dialogResult);
        }

        [RelayCommand]
        private async Task DeleteSignatureAsync(AccountSignature signatureModel)
        {
            var shouldRemove = await _dialogService.ShowConfirmationDialogAsync(string.Format(Translator.SignatureDeleteDialog_Message, signatureModel.Name), Translator.SignatureDeleteDialog_Title, Translator.Buttons_Delete);

            if (!shouldRemove) return;

            Signatures.Remove(signatureModel);
            await _signatureService.DeleteSignatureAsync(signatureModel);
        }
    }
}
