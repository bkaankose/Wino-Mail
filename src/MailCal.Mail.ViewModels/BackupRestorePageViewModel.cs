using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MailCal.Core.Domain.Models.LocalBackup;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Accounts;
using MailCal.Core.Domain.Models.Navigation;
using MailCal.Core.ViewModels;
using MailCal.Messaging.Client.Navigation;
using MailCal.Messaging.UI;

namespace MailCal.Mail.ViewModels;

/// <summary>
/// The single home for backups: an encrypted file on this PC.
/// This is app-wide data, so it lives under General rather than with a single account.
/// </summary>
public partial class BackupRestorePageViewModel : CoreBaseViewModel
{

    private readonly IMailDialogService _dialogService;
    private readonly ILocalBackupService _syncService;

    public BackupRestorePageViewModel(IMailDialogService dialogService,
                                      ILocalBackupService syncService)
    {
        _dialogService = dialogService;
        _syncService = syncService;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportLocalDataCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportLocalDataCommand))]
    public partial bool IsDataTransferInProgress { get; set; }

    [RelayCommand(CanExecute = nameof(CanTransferLocalData))]
    private async Task ExportLocalDataAsync()
    {
        try
        {
            // Write to exactly the path the platform returns. On macOS this is the URL of the save
            // panel, the only location the sandbox grants; on Windows it is the picked folder plus this name.
            var suggestedFileName = MailCalAccountSyncFileExportResult.CreateFileName(DateTime.Now);
            var exportPath = await ExecuteUIThreadAsync(
                () => _dialogService.PickFilePathAsync(suggestedFileName))
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(exportPath))
            {
                return;
            }

            await ExecuteUIThread(() => IsDataTransferInProgress = true);

            var exportResult = await _syncService.ExportToFileAsync(new(), PromptSyncSecretAsync).ConfigureAwait(false);
            await File.WriteAllBytesAsync(exportPath, exportResult.Content).ConfigureAwait(false);

            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Info,
                $"{BuildExportSuccessMessage(exportResult.ExportResult)} {string.Format(Translator.MailCalAccount_Management_LocalDataSaved, exportPath)}",
                InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error);
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
                () => _dialogService.PickWindowsFileContentAsync(SyncSnapshotFormat.FileExtension, SyncSnapshotFormat.LegacyFileExtension))
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
                Translator.MailCalAccount_Management_LocalDataInvalidFile,
                InfoBarMessageType.Error);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error);
        }
        finally
        {
            await ExecuteUIThread(() => IsDataTransferInProgress = false);
        }
    }

    private bool CanTransferLocalData() => !IsDataTransferInProgress;

    private Task<string?> PromptSyncSecretAsync(SyncSnapshotSecretRequest request)
        => ExecuteUIThreadAsync(() => _dialogService.ShowBackupPasswordDialogAsync(request));

    private static string BuildExportSuccessMessage(MailCalAccountSyncExportResult result)
    {
        var parts = new Collection<string>();

        if (result.IncludedPreferences)
        {
            parts.Add(Translator.MailCalAccount_Management_ExportPreferencesSucceeded);
        }

        if (result.IncludedAccounts)
        {
            parts.Add(string.Format(Translator.MailCalAccount_Management_ExportAccountsSucceeded, result.ExportedMailboxCount));
        }

        if (result.ExportedAccountDataCount > 0)
        {
            parts.Add(string.Format(Translator.MailCalAccount_Management_ExportAccountDataSucceeded, result.ExportedAccountDataCount));
        }

        if (result.ExportedAppDataCount > 0)
        {
            parts.Add(string.Format(Translator.MailCalAccount_Management_ExportAppDataSucceeded, result.ExportedAppDataCount));
        }

        if (parts.Count == 0)
        {
            parts.Add(Translator.MailCalAccount_Management_ExportSucceeded);
        }

        return string.Join(" ", parts);
    }

    private static string BuildImportMessage(MailCalAccountSyncImportResult result)
    {
        var parts = new Collection<string>();

        if (result.HadRemotePreferences)
        {
            parts.Add(result.FailedPreferenceCount > 0
                ? string.Format(Translator.MailCalAccount_Management_ImportPartial, result.AppliedPreferenceCount, result.FailedPreferenceCount)
                : string.Format(Translator.MailCalAccount_Management_ImportPreferencesSucceeded, result.AppliedPreferenceCount));
        }

        if (result.ImportedMailboxCount > 0)
        {
            parts.Add(string.Format(Translator.MailCalAccount_Management_ImportAccountsSucceeded, result.ImportedMailboxCount));
        }

        if (result.SkippedDuplicateMailboxCount > 0)
        {
            parts.Add(string.Format(Translator.MailCalAccount_Management_ImportDuplicateAccountsSkipped_Device, result.SkippedDuplicateMailboxCount));
        }

        if (result.AppliedAccountDataCount > 0)
        {
            parts.Add(string.Format(Translator.MailCalAccount_Management_ImportAccountDataSucceeded, result.AppliedAccountDataCount));
        }

        if (result.AppliedAppDataCount > 0)
        {
            parts.Add(string.Format(Translator.MailCalAccount_Management_ImportAppDataSucceeded, result.AppliedAppDataCount));
        }

        if (parts.Count == 0)
        {
            parts.Add(Translator.MailCalAccount_Management_ImportEmpty);
        }

        if (result.ImportedMailboxCount > 0)
        {
            parts.Add(Translator.MailCalAccount_Management_ImportReloginReminder);
        }

        return string.Join(" ", parts);
    }
}
