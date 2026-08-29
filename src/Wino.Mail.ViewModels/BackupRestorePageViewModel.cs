using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.ViewModels;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Moves Wino's accounts and preferences between installs through a single JSON file.
/// This is app-wide data, so it lives under General rather than with a single account.
/// </summary>
public partial class BackupRestorePageViewModel : CoreBaseViewModel
{
    private const string LocalExportFileName = "wino-data-export.json";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    private readonly IMailDialogService _dialogService;
    private readonly IWinoAccountDataSyncService _syncService;

    public BackupRestorePageViewModel(IMailDialogService dialogService, IWinoAccountDataSyncService syncService)
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
            var exportPath = await ExecuteUIThreadAsync(
                () => _dialogService.PickFilePathAsync(LocalExportFileName))
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(exportPath))
            {
                return;
            }

            await ExecuteUIThread(() => IsDataTransferInProgress = true);

            var exportResult = await _syncService.ExportToJsonAsync(new()).ConfigureAwait(false);
            await File.WriteAllTextAsync(exportPath, exportResult.JsonContent, Utf8WithoutBom).ConfigureAwait(false);

            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Info,
                $"{BuildExportSuccessMessage(exportResult.ExportResult)} {string.Format(Translator.WinoAccount_Management_LocalDataSaved, exportPath)}",
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
                () => _dialogService.PickWindowsFileContentAsync(".json"))
                .ConfigureAwait(false);

            if (fileContent.Length == 0)
            {
                return;
            }

            await ExecuteUIThread(() => IsDataTransferInProgress = true);

            var jsonContent = Encoding.UTF8.GetString(fileContent);
            var result = await _syncService.ImportFromJsonAsync(jsonContent).ConfigureAwait(false);

            var messageType = result.FailedPreferenceCount > 0
                ? InfoBarMessageType.Warning
                : InfoBarMessageType.Success;

            _dialogService.InfoBarMessage(
                result.FailedPreferenceCount > 0 ? Translator.GeneralTitle_Warning : Translator.GeneralTitle_Info,
                BuildImportMessage(result),
                messageType);
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
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error);
        }
        finally
        {
            await ExecuteUIThread(() => IsDataTransferInProgress = false);
        }
    }

    private bool CanTransferLocalData() => !IsDataTransferInProgress;

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

        if (parts.Count == 0)
        {
            parts.Add(Translator.WinoAccount_Management_ImportEmpty);
        }

        return string.Join(" ", parts);
    }
}
