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
    public bool HasLoadedAccount => isLoaded && Account != null;

    private readonly IMailDialogService _dialogService = dialogService;
    private readonly ISignatureService _signatureService = signatureService;
    private readonly IAccountService _accountService = accountService;

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
        => await InitializeAsync(mode, parameters);

    public async Task InitializeAsync(NavigationMode mode, object parameters)
    {
        await DrainPreferenceWritesAsync();
        base.OnNavigatedTo(mode, parameters);
        isLoaded = false;
        PreferenceError = string.Empty;
        Account = null;

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

    private AccountSettingsWriteLifetime _preferenceWrites;
    public bool HasPendingPreferenceWrites => _preferenceWrites?.HasPending == true;
    public Task DrainPreferenceWritesAsync() => _preferenceWrites?.DrainAsync() ?? Task.CompletedTask;
    [ObservableProperty] public partial string PreferenceError { get; set; } = string.Empty;
    private AccountSettingsWriteLifetime PreferenceWrites => _preferenceWrites ??= new(_ =>
        ExecuteUIThread(() => PreferenceError = Translator.MacOSMail_OperationFailed));

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!isLoaded || Account?.Preferences == null) return;

        var account = Account;
        switch (e.PropertyName)
        {
            case nameof(IsSignatureEnabled):
                var enabled = IsSignatureEnabled;
                PreferenceWrites.Enqueue(async () =>
                {
                    account.Preferences.IsSignatureEnabled = enabled;
                    await _accountService.UpdateAccountAsync(account);
                });
                break;
            case nameof(SelectedSignatureForNewMessages):
                var newSignature = GetPersistedSignatureId(SelectedSignatureForNewMessages);
                PreferenceWrites.Enqueue(async () =>
                {
                    account.Preferences.SignatureIdForNewMessages = newSignature;
                    await _accountService.UpdateAccountAsync(account);
                });
                break;
            case nameof(SelectedSignatureForFollowingMessages):
                var followingSignature = GetPersistedSignatureId(SelectedSignatureForFollowingMessages);
                PreferenceWrites.Enqueue(async () =>
                {
                    account.Preferences.SignatureIdForFollowingMessages = followingSignature;
                    await _accountService.UpdateAccountAsync(account);
                });
                break;
        }
    }
    [RelayCommand]
    private async Task OpenSignatureEditorCreateAsync()
    {
        var dialogResult = await _dialogService.ShowSignatureEditorDialog();

        if (dialogResult == null) return;

        dialogResult.MailAccountId = Account.Id;
        await _signatureService.CreateSignatureAsync(dialogResult);
        await ExecuteUIThread(() => Signatures.Add(dialogResult));
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
        await _signatureService.UpdateSignatureAsync(dialogResult);

        await ExecuteUIThread(() =>
        {
            Signatures[indexOfCurrentSignature] = dialogResult;

            if (wasSelectedForNewMessages)
                SelectedSignatureForNewMessages = dialogResult;

            if (wasSelectedForFollowingMessages)
                SelectedSignatureForFollowingMessages = dialogResult;
        });

    }

    [RelayCommand]
    private async Task DeleteSignatureAsync(AccountSignature signatureModel)
    {
        var shouldRemove = await _dialogService.ShowConfirmationDialogAsync(string.Format(Translator.SignatureDeleteDialog_Message, signatureModel.Name), Translator.SignatureDeleteDialog_Title, Translator.Buttons_Delete);

        if (!shouldRemove) return;

        await _signatureService.DeleteSignatureAsync(signatureModel);

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

    }

    public async Task ReleaseAccountAsync()
    {
        await DrainPreferenceWritesAsync();
        isLoaded = false;
        Account = null;
        SelectedSignatureForNewMessages = null;
        SelectedSignatureForFollowingMessages = null;
        Signatures.Clear();
    }
    private Guid? GetPersistedSignatureId(AccountSignature signature)
        => signature?.Id is Guid signatureId && signatureId != EmptyGuid
            ? signatureId
            : null;

    private AccountSignature GetNoneSignature()
        => Signatures.FirstOrDefault(x => x.Id == EmptyGuid)
           ?? new AccountSignature { Id = EmptyGuid, Name = Translator.SettingsSignature_NoneSignatureName };
}
