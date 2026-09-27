using System;
using System.Diagnostics;
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
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

public partial class WelcomePageV2ViewModel : MailBaseViewModel
{
    private readonly IMailDialogService _dialogService;
    private readonly IWinoAccountDataSyncService _syncService;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GetStartedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportFromWinoAccountCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportFromJsonCommand))]
    public partial bool IsImportInProgress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportStatus))]
    public partial string ImportStatusMessage { get; set; } = string.Empty;

    public bool HasImportStatus => !string.IsNullOrWhiteSpace(ImportStatusMessage);

    public WelcomePageV2ViewModel(IMailDialogService dialogService,
                                  IWinoAccountDataSyncService syncService)
    {
        _dialogService = dialogService;
        _syncService = syncService;
    }

    [RelayCommand(CanExecute = nameof(CanOpenWelcomeActions))]
    private void GetStarted()
    {
        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.WelcomeWizard_Step2Title,
            WinoPage.ProviderSelectionPage,
            ProviderSelectionNavigationContext.CreateForWizard()));
    }

    [RelayCommand(CanExecute = nameof(CanOpenWelcomeActions))]
    private async Task ImportFromWinoAccountAsync()
    {
        await ExecuteUIThread(() => ImportStatusMessage = string.Empty);

        try
        {
            var account = await ExecuteUIThreadAsync(
                _dialogService.ShowWinoAccountLoginDialogAsync).ConfigureAwait(false);
            if (account == null)
            {
                return;
            }

            await ExecuteUIThread(() => IsImportInProgress = true);

            var result = await _syncService.ImportAsync(new WinoAccountSyncSelection(), PromptSyncSecretAsync).ConfigureAwait(false);
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
        catch (Exception ex)
        {
            await ExecuteUIThreadAsync(() =>
                _dialogService.ShowMessageAsync(
                    WinoAccountApiErrorTranslator.Describe(ex),
                    Translator.GeneralTitle_Error,
                    WinoCustomMessageDialogIcon.Error)).ConfigureAwait(false);
        }
        finally
        {
            await ExecuteUIThread(() => IsImportInProgress = false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenWelcomeActions))]
    private async Task ImportFromJsonAsync()
    {
        await ExecuteUIThread(() => ImportStatusMessage = string.Empty);

        try
        {
            var fileContent = await _dialogService.PickWindowsFileContentAsync(".winosnap", ".json");
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
                Translator.WinoAccount_Management_LocalDataInvalidFile,
                Translator.GeneralTitle_Error,
                WinoCustomMessageDialogIcon.Error);
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync(WinoAccountApiErrorTranslator.Describe(ex), Translator.GeneralTitle_Error, WinoCustomMessageDialogIcon.Error);
        }
        finally
        {
            await ExecuteUIThread(() => IsImportInProgress = false);
        }
    }

    private bool CanOpenWelcomeActions() => !IsImportInProgress;

    private Task<string?> PromptSyncSecretAsync(SyncSnapshotSecretRequest request)
        => ExecuteUIThreadAsync(() => _dialogService.ShowWinoAccountSyncSecretDialogAsync(request));

    private static string BuildInlineImportMessage(WinoAccountSyncImportResult result)
    {
        var preferencesMessage = result.FailedPreferenceCount > 0
            ? string.Format(Translator.WinoAccount_Management_ImportPartial, result.AppliedPreferenceCount, result.FailedPreferenceCount)
            : result.HadRemotePreferences
                ? string.Format(Translator.WinoAccount_Management_ImportPreferencesSucceeded, result.AppliedPreferenceCount)
                : string.Empty;

        if (result.AppliedAccountDataCount > 0)
        {
            var accountDataMessage = string.Format(Translator.WinoAccount_Management_ImportAccountDataSucceeded, result.AppliedAccountDataCount);
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
            ? Translator.WinoAccount_Management_ImportEmpty
            : preferencesMessage;
    }
}
