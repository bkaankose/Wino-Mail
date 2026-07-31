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

namespace Wino.Mail.ViewModels;

public partial class SignatureManagementPageViewModel(IMailDialogService dialogService,
                                        ISignatureService signatureService,
                                        IAccountService accountService) : MailBaseViewModel
{
    public ObservableCollection<AccountSignature> Signatures { get; set; } = [];
    private bool isLoaded;

    [ObservableProperty]
    public partial bool IsSignatureEnabled { get; set; }

    public Guid EmptyGuid { get; } = Guid.Empty;

    [ObservableProperty]
    public partial AccountSignature SelectedSignatureForNewMessages { get; set; }

    [ObservableProperty]
    public partial AccountSignature SelectedSignatureForFollowingMessages { get; set; }

    private MailAccount Account { get; set; }

    private readonly IMailDialogService _dialogService = dialogService;
    private readonly ISignatureService _signatureService = signatureService;
    private readonly IAccountService _accountService = accountService;

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        isLoaded = false;

        if (parameters is Guid accountId)
            Account = await _accountService.GetAccountAsync(accountId);

        if (Account == null) return;

        var dbSignatures = await _signatureService.GetSignaturesAsync(Account.Id);
        var noneSignature = new AccountSignature { Id = EmptyGuid, Name = Translator.SettingsSignature_NoneSignatureName };
        var signatureForNewMessages = dbSignatures.FirstOrDefault(x => x.Id == Account.Preferences.SignatureIdForNewMessages) ?? noneSignature;
        var signatureForFollowingMessages = dbSignatures.FirstOrDefault(x => x.Id == Account.Preferences.SignatureIdForFollowingMessages) ?? noneSignature;

        await ExecuteUIThread(() =>
        {
            IsSignatureEnabled = Account.Preferences.IsSignatureEnabled;

            Signatures.Clear();
            Signatures.Add(noneSignature);
            dbSignatures.ForEach(Signatures.Add);

            SelectedSignatureForNewMessages = signatureForNewMessages;
            SelectedSignatureForFollowingMessages = signatureForFollowingMessages;
        });

        isLoaded = true;
    }

    protected override async void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (!isLoaded || Account?.Preferences == null) return;

        switch (e.PropertyName)
        {
            case nameof(IsSignatureEnabled):
                Account.Preferences.IsSignatureEnabled = IsSignatureEnabled;
                await _accountService.UpdateAccountAsync(Account);
                break;
            case nameof(SelectedSignatureForNewMessages):
                Account.Preferences.SignatureIdForNewMessages = GetPersistedSignatureId(SelectedSignatureForNewMessages);
                await _accountService.UpdateAccountAsync(Account);
                break;
            case nameof(SelectedSignatureForFollowingMessages):
                Account.Preferences.SignatureIdForFollowingMessages = GetPersistedSignatureId(SelectedSignatureForFollowingMessages);
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
        await ExecuteUIThread(() => Signatures.Add(dialogResult));
        await _signatureService.CreateSignatureAsync(dialogResult);
    }

    [RelayCommand]
    private async Task OpenSignatureEditorEditAsync(AccountSignature signatureModel)
    {
        var dialogResult = await _dialogService.ShowSignatureEditorDialog(signatureModel);

        if (dialogResult == null) return;

        var indexOfCurrentSignature = Signatures.IndexOf(signatureModel);
        if (indexOfCurrentSignature < 0) return;

        var wasSelectedForNewMessages = SelectedSignatureForNewMessages?.Id == signatureModel.Id;
        var wasSelectedForFollowingMessages = SelectedSignatureForFollowingMessages?.Id == signatureModel.Id;

        dialogResult.MailAccountId = signatureModel.MailAccountId;

        await ExecuteUIThread(() =>
        {
            Signatures[indexOfCurrentSignature] = dialogResult;

            if (wasSelectedForNewMessages)
                SelectedSignatureForNewMessages = dialogResult;

            if (wasSelectedForFollowingMessages)
                SelectedSignatureForFollowingMessages = dialogResult;
        });

        await _signatureService.UpdateSignatureAsync(dialogResult);
    }

    [RelayCommand]
    private async Task DeleteSignatureAsync(AccountSignature signatureModel)
    {
        var shouldRemove = await _dialogService.ShowConfirmationDialogAsync(string.Format(Translator.SignatureDeleteDialog_Message, signatureModel.Name), Translator.SignatureDeleteDialog_Title, Translator.Buttons_Delete);

        if (!shouldRemove) return;

        var shouldResetNewMessagesSignature = SelectedSignatureForNewMessages?.Id == signatureModel.Id;
        var shouldResetFollowingMessagesSignature = SelectedSignatureForFollowingMessages?.Id == signatureModel.Id;

        await ExecuteUIThread(() =>
        {
            Signatures.Remove(signatureModel);

            var noneSignature = GetNoneSignature();

            if (shouldResetNewMessagesSignature)
                SelectedSignatureForNewMessages = noneSignature;

            if (shouldResetFollowingMessagesSignature)
                SelectedSignatureForFollowingMessages = noneSignature;
        });

        await _signatureService.DeleteSignatureAsync(signatureModel);
    }

    private Guid? GetPersistedSignatureId(AccountSignature signature)
        => signature?.Id is Guid signatureId && signatureId != EmptyGuid
            ? signatureId
            : null;

    private AccountSignature GetNoneSignature()
        => Signatures.FirstOrDefault(x => x.Id == EmptyGuid)
           ?? new AccountSignature { Id = EmptyGuid, Name = Translator.SettingsSignature_NoneSignatureName };
}
