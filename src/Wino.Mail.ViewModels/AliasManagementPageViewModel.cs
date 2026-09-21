using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmailValidation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Services;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

public partial class AliasManagementPageViewModel : MailBaseViewModel
{
    private readonly IMailDialogService _dialogService;
    private readonly IAccountService _accountService;
    private readonly ISmimeCertificateService _smimeCertificateService;
    private readonly IWinoLogger _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSynchronizeAliases))]
    public partial MailAccount Account { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AliasItems))]
    [NotifyPropertyChangedFor(nameof(HasAliases))]
    [NotifyPropertyChangedFor(nameof(IsEmptyStateVisible))]
    [NotifyPropertyChangedFor(nameof(AliasSummary))]
    public partial List<MailAccountAlias> AccountAliases { get; set; } = [];

    /// <summary>Set only around the first load, so a reload after a write never blanks the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyStateVisible))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsSynchronizing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSynchronizationError))]
    public partial string SynchronizationError { get; set; }

    public bool CanSynchronizeAliases => Account?.IsAliasSyncSupported ?? false;

    public IReadOnlyList<AliasManagementItem> AliasItems => AliasManagementItem.Create(AccountAliases);

    public bool HasAliases => AccountAliases.Count > 0;

    /// <summary>The empty state replaces the list only once loading has settled; otherwise it would flash on every open.</summary>
    public bool IsEmptyStateVisible => !IsLoading && AccountAliases.Count == 0;

    public bool HasSynchronizationError => !string.IsNullOrEmpty(SynchronizationError);

    /// <summary>
    /// The one-line count above the list. It names the two states a user has to act on, and stays
    /// silent about them when every alias is fine.
    /// </summary>
    public string AliasSummary
    {
        get
        {
            var aliases = AccountAliases;
            if (aliases.Count == 0) return string.Empty;

            var parts = new List<string>
            {
                aliases.Count == 1
                    ? Translator.AccountAlias_Summary_AliasCountSingle
                    : string.Format(Translator.AccountAlias_Summary_AliasCountPlural, aliases.Count)
            };

            var confirmed = aliases.Count(alias => alias.IsCapabilityConfirmed);
            var unknown = aliases.Count(alias => alias.IsCapabilityUnknown);
            var denied = aliases.Count(alias => alias.IsCapabilityDenied);

            if (confirmed > 0) parts.Add(string.Format(Translator.AccountAlias_Summary_Confirmed, confirmed));
            if (unknown > 0) parts.Add(string.Format(Translator.AccountAlias_Summary_Unknown, unknown));
            if (denied > 0) parts.Add(string.Format(Translator.AccountAlias_Summary_Denied, denied));

            return string.Join(" • ", parts);
        }
    }

    public AliasManagementPageViewModel(IMailDialogService dialogService,
                                        IAccountService accountService,
                                        ISmimeCertificateService smimeCertificateService,
                                        IWinoLogger logger)
    {
        _dialogService = dialogService;
        _accountService = accountService;
        _smimeCertificateService = smimeCertificateService;
        _logger = logger;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (parameters is Guid accountId)
            Account = await _accountService.GetAccountAsync(accountId);

        if (Account == null) return;

        IsLoading = AccountAliases.Count == 0;

        try
        {
            await LoadAliasesAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadAliasesAsync()
    {
        var aliases = await _accountService.GetAccountAliasesAsync(Account.Id);
        foreach (var alias in aliases)
        {
            alias.Certificates.Clear();
            alias.Certificates.Add(null); // First blank optioon
            var certs = _smimeCertificateService.GetCertificates()
                .Where(cert => cert.Subject.Contains(alias.AliasAddress, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var cert in certs)
                alias.Certificates.Add(cert);

            alias.SelectedSigningCertificate = !string.IsNullOrEmpty(alias.SelectedSigningCertificateThumbprint)
                ? alias.Certificates.FirstOrDefault(c => c?.Thumbprint == alias.SelectedSigningCertificateThumbprint)
                : null;
        }
        AccountAliases = aliases;
    }

    [RelayCommand]
    private Task SetAliasPrimaryAsync(MailAccountAlias alias)
        // A targeted write, not a resubmitted list: the copy this page holds may already be
        // behind an alias sync, and writing it back would undo whatever that sync brought in.
        => WriteAliasSettingAsync(() => _accountService.SetDefaultAccountAliasAsync(Account.Id, alias.Id));

    [RelayCommand]
    private async Task SyncAliasesAsync()
    {
        if (!CanSynchronizeAliases) return;

        // The failure lands in the page's own InfoBar next to the list it affects, not in a transient message.
        SynchronizationError = string.Empty;
        IsSynchronizing = true;

        try
        {
            var aliasSyncResult = await SynchronizationManager.Instance.SynchronizeAliasesAsync(Account.Id);

            if (aliasSyncResult.CompletedState == SynchronizationCompletedState.Success)
                await LoadAliasesAsync();
            else
                SynchronizationError = Translator.Exception_FailedToSynchronizeAliases;
        }
        catch (Exception ex)
        {
            _logger.CaptureException(ex, "SynchronizeAccountAliases");
            SynchronizationError = ex.Message;
        }
        finally
        {
            IsSynchronizing = false;
        }
    }

    [RelayCommand]
    private async Task AddNewAliasAsync()
    {
        var createdAliasDialog = await _dialogService.ShowCreateAccountAliasDialogAsync();

        if (createdAliasDialog.CreatedAccountAlias == null) return;

        var newAlias = createdAliasDialog.CreatedAccountAlias;

        // Check existence.
        if (AccountAliases.Any(a => a.AliasAddress == newAlias.AliasAddress))
        {
            await _dialogService.ShowMessageAsync(Translator.DialogMessage_AliasExistsTitle,
                                                 Translator.DialogMessage_AliasExistsMessage,
                                                 WinoCustomMessageDialogIcon.Warning);
            return;
        }

        // Validate all addresses.
        if (!EmailValidator.Validate(newAlias.AliasAddress) || (!string.IsNullOrEmpty(newAlias.ReplyToAddress) && !EmailValidator.Validate(newAlias.ReplyToAddress)))
        {
            await _dialogService.ShowMessageAsync(Translator.DialogMessage_InvalidAliasMessage,
                                                 Translator.DialogMessage_InvalidAliasTitle,
                                                 WinoCustomMessageDialogIcon.Warning);
            return;
        }

        newAlias.AccountId = Account.Id;

        // The service decides: another window, or an alias sync, may have added this address
        // between the check above and this call.
        var isCreated = await _accountService.AddAccountAliasAsync(Account.Id, newAlias);

        if (isCreated)
        {
            _dialogService.InfoBarMessage(Translator.DialogMessage_AliasCreatedTitle, Translator.DialogMessage_AliasCreatedMessage, InfoBarMessageType.Success);
        }
        else
        {
            await _dialogService.ShowMessageAsync(Translator.DialogMessage_AliasExistsTitle,
                                                 Translator.DialogMessage_AliasExistsMessage,
                                                 WinoCustomMessageDialogIcon.Warning);
        }

        await LoadAliasesAsync();
    }

    [RelayCommand]
    private async Task DeleteAliasAsync(MailAccountAlias alias)
    {
        // Primary aliases can't be deleted.
        if (alias.IsPrimary)
        {
            await _dialogService.ShowMessageAsync(Translator.Info_CantDeletePrimaryAliasMessage,
                                                 Translator.GeneralTitle_Warning,
                                                 WinoCustomMessageDialogIcon.Warning);
            return;
        }

        // Root aliases can't be deleted.
        if (alias.IsRootAlias)
        {
            await _dialogService.ShowMessageAsync(Translator.DialogMessage_CantDeleteRootAliasTitle,
                                                 Translator.DialogMessage_CantDeleteRootAliasMessage,
                                                 WinoCustomMessageDialogIcon.Warning);
            return;
        }

        await _accountService.DeleteAccountAliasAsync(alias.Id);
        await LoadAliasesAsync();
    }

    public Task SetAliasSmimeEncryption(MailAccountAlias alias, bool value)
        => WriteAliasSettingAsync(() => _accountService.SetAliasEncryptionAsync(Account.Id, alias.Id, value));

    public Task SetSelectedSigningCertificate(MailAccountAlias alias, X509Certificate2 cert)
        => WriteAliasSettingAsync(() => _accountService.SetAliasSigningCertificateAsync(Account.Id, alias.Id, cert?.Thumbprint));

    /// <summary>
    /// Runs one targeted alias write and then reloads, whether it succeeded or not, so the page
    /// shows what is stored rather than the change the user attempted.
    /// </summary>
    private async Task WriteAliasSettingAsync(Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (Exception exception)
        {
            _logger?.CaptureException(exception, nameof(WriteAliasSettingAsync));
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, exception.Message, InfoBarMessageType.Error);
        }
        finally
        {
            await LoadAliasesAsync();
        }
    }
}
