using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The single home for backups: a file on this PC, and the encrypted copy in the Wino Account.
/// This is app-wide data, so it lives under General rather than with a single account.
/// </summary>
public partial class BackupRestorePageViewModel : CoreBaseViewModel,
    IRecipient<WinoAccountProfileUpdatedMessage>,
    IRecipient<WinoAccountProfileDeletedMessage>
{
    private const string LocalExportFileName = "wino-backup.winosnap";

    private readonly IMailDialogService _dialogService;
    private readonly IWinoAccountDataSyncService _syncService;
    private readonly IWinoAccountProfileService _profileService;

    public BackupRestorePageViewModel(IMailDialogService dialogService,
                                      IWinoAccountDataSyncService syncService,
                                      IWinoAccountProfileService profileService)
    {
        _dialogService = dialogService;
        _syncService = syncService;
        _profileService = profileService;
    }

    /// <summary>
    /// One flag for both backups: a file transfer and a Wino Account transfer never run together.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportLocalDataCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportLocalDataCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportToWinoAccountCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportFromWinoAccountCommand))]
    public partial bool IsDataTransferInProgress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWinoAccountSignedOut))]
    [NotifyCanExecuteChangedFor(nameof(ExportToWinoAccountCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportFromWinoAccountCommand))]
    public partial bool IsWinoAccountSignedIn { get; set; }

    /// <summary>
    /// False until the sign-in state is known, so neither Wino Account card flashes on load.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWinoAccountSignedOut))]
    public partial bool IsWinoAccountStateLoaded { get; set; }

    public bool IsWinoAccountSignedOut => IsWinoAccountStateLoaded && !IsWinoAccountSignedIn;

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        _ = LoadWinoAccountStateAsync();
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        Messenger.Register<WinoAccountProfileUpdatedMessage>(this);
        Messenger.Register<WinoAccountProfileDeletedMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<WinoAccountProfileUpdatedMessage>(this);
        Messenger.Unregister<WinoAccountProfileDeletedMessage>(this);
    }

    public void Receive(WinoAccountProfileUpdatedMessage message)
        => _ = LoadWinoAccountStateAsync();

    public void Receive(WinoAccountProfileDeletedMessage message)
        => _ = LoadWinoAccountStateAsync();

    private async Task LoadWinoAccountStateAsync()
    {
        var isSignedIn = false;

        try
        {
            isSignedIn = await _profileService.HasActiveAccountAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Treat an unreadable profile as signed out. The sign-in card still leads somewhere useful.
        }

        await ExecuteUIThread(() =>
        {
            IsWinoAccountSignedIn = isSignedIn;
            IsWinoAccountStateLoaded = true;
        });
    }

    [RelayCommand]
    private void OpenWinoAccountManagement()
        => Messenger.Send(new SettingsRootNavigationRequested(WinoPage.WinoAccountManagementPage));

    [RelayCommand(CanExecute = nameof(CanTransferWinoAccountData))]
    private async Task ExportToWinoAccountAsync()
    {
        try
        {
            var result = await _dialogService.ShowWinoAccountExportDialogAsync().ConfigureAwait(false);
            if (result == null)
            {
                return;
            }

            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Info,
                BuildExportSuccessMessage(result),
                InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                WinoAccountApiErrorTranslator.Describe(ex),
                InfoBarMessageType.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanTransferWinoAccountData))]
    private async Task ImportFromWinoAccountAsync()
    {
        await ExecuteUIThread(() => IsDataTransferInProgress = true);

        try
        {
            var result = await _syncService.ImportAsync(new WinoAccountSyncSelection(), PromptSyncSecretAsync).ConfigureAwait(false);

            if (result.Appearance != null)
            {
                await ExecuteUIThread(() => _syncService.ApplyAppearance(result.Appearance));
            }

            if (!result.HasAnyRemoteData)
            {
                _dialogService.InfoBarMessage(
                    Translator.GeneralTitle_Info,
                    Translator.WinoAccount_Management_NoRemoteSettings,
                    InfoBarMessageType.Information);
                return;
            }

            var messageType = result.FailedPreferenceCount > 0
                ? InfoBarMessageType.Warning
                : InfoBarMessageType.Success;

            _dialogService.InfoBarMessage(
                result.FailedPreferenceCount > 0 ? Translator.GeneralTitle_Warning : Translator.GeneralTitle_Info,
                BuildImportMessage(result),
                messageType);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                WinoAccountApiErrorTranslator.Describe(ex),
                InfoBarMessageType.Error);
        }
        finally
        {
            await ExecuteUIThread(() => IsDataTransferInProgress = false);
        }
    }

    private bool CanTransferWinoAccountData() => IsWinoAccountSignedIn && !IsDataTransferInProgress;

    [RelayCommand(CanExecute = nameof(CanTransferLocalData))]
    private async Task ExportLocalDataAsync()
    {
        try
        {
            var exportPath = await ExecuteUIThreadAsync(
                () => _dialogService.PickFilePathAsync(LocalExportFileName))
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(exportPath))
            {
                return;
            }

            await ExecuteUIThread(() => IsDataTransferInProgress = true);

            var exportResult = await _syncService.ExportToFileAsync(new(), PromptSyncSecretAsync).ConfigureAwait(false);
            exportPath = Path.Combine(Path.GetDirectoryName(exportPath) ?? exportPath, exportResult.FileName);
            await File.WriteAllBytesAsync(exportPath, exportResult.Content).ConfigureAwait(false);

            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Info,
                $"{BuildExportSuccessMessage(exportResult.ExportResult)} {string.Format(Translator.WinoAccount_Management_LocalDataSaved, exportPath)}",
                InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, WinoAccountApiErrorTranslator.Describe(ex), InfoBarMessageType.Error);
        }
        finally
        {
            await ExecuteUIThread(() => IsDataTransferInProgress = false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanTransferLocalData))]
    private async Task ImportLocalDataAsync()
    {
        try
        {
            var fileContent = await ExecuteUIThreadAsync(
                () => _dialogService.PickWindowsFileContentAsync(".winosnap", ".json"))
                .ConfigureAwait(false);

            if (fileContent.Length == 0)
            {
                return;
            }

            await ExecuteUIThread(() => IsDataTransferInProgress = true);

            await ExecuteUIThreadAsync(async () =>
            {
                var result = await _syncService.ImportFromFileAsync(fileContent, PromptSyncSecretAsync).ConfigureAwait(false);
                if (result.Appearance != null)
                {
                    await ExecuteUIThread(() => _syncService.ApplyAppearance(result.Appearance));
                }

                var messageType = result.FailedPreferenceCount > 0
               ? InfoBarMessageType.Warning
               : InfoBarMessageType.Success;

                _dialogService.InfoBarMessage(
                    result.FailedPreferenceCount > 0 ? Translator.GeneralTitle_Warning : Translator.GeneralTitle_Info,
                    BuildImportMessage(result),
                    messageType);
            });
        }
        catch (JsonException)
        {
            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                Translator.WinoAccount_Management_LocalDataInvalidFile,
                InfoBarMessageType.Error);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, WinoAccountApiErrorTranslator.Describe(ex), InfoBarMessageType.Error);
        }
        finally
        {
            await ExecuteUIThread(() => IsDataTransferInProgress = false);
        }
    }

    private bool CanTransferLocalData() => !IsDataTransferInProgress;

    private Task<string?> PromptSyncSecretAsync(SyncSnapshotSecretRequest request)
        => ExecuteUIThreadAsync(() => _dialogService.ShowWinoAccountSyncSecretDialogAsync(request));

    private static string BuildExportSuccessMessage(WinoAccountSyncExportResult result)
    {
        var parts = new Collection<string>();

        if (result.IncludedPreferences)
        {
            parts.Add(Translator.WinoAccount_Management_ExportPreferencesSucceeded);
        }

        if (result.IncludedAccounts)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ExportAccountsSucceeded, result.ExportedMailboxCount));
        }

        if (result.ExportedAccountDataCount > 0)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ExportAccountDataSucceeded, result.ExportedAccountDataCount));
        }

        if (result.ExportedAppDataCount > 0)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ExportAppDataSucceeded, result.ExportedAppDataCount));
        }

        if (parts.Count == 0)
        {
            parts.Add(Translator.WinoAccount_Management_ExportSucceeded);
        }

        return string.Join(" ", parts);
    }

    private static string BuildImportMessage(WinoAccountSyncImportResult result)
    {
        var parts = new Collection<string>();

        if (result.HadRemotePreferences)
        {
            parts.Add(result.FailedPreferenceCount > 0
                ? string.Format(Translator.WinoAccount_Management_ImportPartial, result.AppliedPreferenceCount, result.FailedPreferenceCount)
                : string.Format(Translator.WinoAccount_Management_ImportPreferencesSucceeded, result.AppliedPreferenceCount));
        }

        if (result.ImportedMailboxCount > 0)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ImportAccountsSucceeded, result.ImportedMailboxCount));
        }

        if (result.SkippedDuplicateMailboxCount > 0)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ImportDuplicateAccountsSkipped, result.SkippedDuplicateMailboxCount));
        }

        if (result.AppliedAccountDataCount > 0)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ImportAccountDataSucceeded, result.AppliedAccountDataCount));
        }

        if (result.AppliedAppDataCount > 0)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ImportAppDataSucceeded, result.AppliedAppDataCount));
        }

        if (parts.Count == 0)
        {
            parts.Add(Translator.WinoAccount_Management_ImportEmpty);
        }

        if (result.ImportedMailboxCount > 0)
        {
            parts.Add(Translator.WinoAccount_Management_ImportReloginReminder);
        }

        return string.Join(" ", parts);
    }
}
