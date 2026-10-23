using System;
using System.Diagnostics;
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
using MailCal.Mail.ViewModels.Data;
using MailCal.Messaging.Client.Navigation;
using MailCal.Messaging.UI;

namespace MailCal.Mail.ViewModels;

public partial class WelcomePageV2ViewModel : MailBaseViewModel
{
    private readonly IMailDialogService _dialogService;
    private readonly ILocalBackupService _syncService;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GetStartedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportFromJsonCommand))]
    public partial bool IsImportInProgress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportStatus))]
    public partial string ImportStatusMessage { get; set; } = string.Empty;

    public bool HasImportStatus => !string.IsNullOrWhiteSpace(ImportStatusMessage);

    public WelcomePageV2ViewModel(IMailDialogService dialogService,
                                  ILocalBackupService syncService)
    {
        _dialogService = dialogService;
        _syncService = syncService;
    }

    [RelayCommand(CanExecute = nameof(CanOpenWelcomeActions))]
    private void GetStarted()
    {
        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.WelcomeWizard_Step2Title,
            MailCalPage.ProviderSelectionPage,
            ProviderSelectionNavigationContext.CreateForWizard()));
    }

    [RelayCommand(CanExecute = nameof(CanOpenWelcomeActions))]
    private async Task ImportFromJsonAsync()
    {
        await ExecuteUIThread(() => ImportStatusMessage = string.Empty);

        try
        {
            var fileContent = await _dialogService.PickWindowsFileContentAsync(SyncSnapshotFormat.FileExtension, SyncSnapshotFormat.LegacyFileExtension);
            if (fileContent.Length == 0)
            {
                return;
            }

            await ExecuteUIThread(() => IsImportInProgress = true);

            var result = await _syncService.ImportFromFileAsync(fileContent, PromptSyncSecretAsync);
            if (result.ImportedMailboxCount > 0)
            {
                ReportUIChange(new WelcomeImportCompletedMessage(result.ImportedMailboxCount, result.Appearance));
                return;
            }

            if (result.Appearance != null)
            {
                await ExecuteUIThread(() => _syncService.ApplyAppearance(result.Appearance));
            }

            await ExecuteUIThread(() => ImportStatusMessage = BuildInlineImportMessage(result));
        }
        catch (JsonException ex)
        {
            Debug.WriteLine(ex.Message);
            await _dialogService.ShowMessageAsync(
                Translator.MailCalAccount_Management_LocalDataInvalidFile,
                Translator.GeneralTitle_Error,
                MailCalCustomMessageDialogIcon.Error);
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync(ex.Message, Translator.GeneralTitle_Error, MailCalCustomMessageDialogIcon.Error);
        }
        finally
        {
            await ExecuteUIThread(() => IsImportInProgress = false);
        }
    }

    private bool CanOpenWelcomeActions() => !IsImportInProgress;

    private Task<string?> PromptSyncSecretAsync(SyncSnapshotSecretRequest request)
        => ExecuteUIThreadAsync(() => _dialogService.ShowBackupPasswordDialogAsync(request));

    private static string BuildInlineImportMessage(MailCalAccountSyncImportResult result)
    {
        var preferencesMessage = result.FailedPreferenceCount > 0
            ? string.Format(Translator.MailCalAccount_Management_ImportPartial, result.AppliedPreferenceCount, result.FailedPreferenceCount)
            : result.HadRemotePreferences
                ? string.Format(Translator.MailCalAccount_Management_ImportPreferencesSucceeded, result.AppliedPreferenceCount)
                : string.Empty;

        if (result.AppliedAccountDataCount > 0)
        {
            var accountDataMessage = string.Format(Translator.MailCalAccount_Management_ImportAccountDataSucceeded, result.AppliedAccountDataCount);
            preferencesMessage = string.IsNullOrWhiteSpace(preferencesMessage)
                ? accountDataMessage
                : $"{preferencesMessage} {accountDataMessage}";
        }

        if (result.RemoteMailboxCount == 0)
        {
            return string.IsNullOrWhiteSpace(preferencesMessage)
                ? Translator.WelcomeWindow_ImportNoAccountsFound
                : $"{preferencesMessage} {Translator.WelcomeWindow_ImportNoAccountsFound}";
        }

        if (result.SkippedDuplicateMailboxCount > 0 && result.ImportedMailboxCount == 0)
        {
            var duplicateMessage = string.Format(Translator.WelcomeWindow_ImportDuplicateAccountsSkipped, result.SkippedDuplicateMailboxCount);
            return string.IsNullOrWhiteSpace(preferencesMessage)
                ? duplicateMessage
                : $"{preferencesMessage} {duplicateMessage}";
        }

        return string.IsNullOrWhiteSpace(preferencesMessage)
            ? Translator.MailCalAccount_Management_ImportEmpty
            : preferencesMessage;
    }
}
