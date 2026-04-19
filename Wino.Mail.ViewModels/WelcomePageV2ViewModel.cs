using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
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
using Wino.Core.Domain.Models.Updates;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

public partial class WelcomePageV2ViewModel : MailBaseViewModel
{
    private readonly IUpdateManager _updateManager;
    private readonly IMailDialogService _dialogService;
    private readonly IWinoAccountDataSyncService _syncService;

    [ObservableProperty]
    public partial List<UpdateNoteSection> UpdateSections { get; set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GetStartedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportFromWinoAccountCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportFromJsonCommand))]
    public partial bool IsImportInProgress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportStatus))]
    public partial string ImportStatusMessage { get; set; } = string.Empty;

    public bool HasImportStatus => !string.IsNullOrWhiteSpace(ImportStatusMessage);

    public WelcomePageV2ViewModel(IUpdateManager updateManager,
                                  IMailDialogService dialogService,
                                  IWinoAccountDataSyncService syncService)
    {
        _updateManager = updateManager;
        _dialogService = dialogService;
        _syncService = syncService;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        try
        {
            var updateNotes = await _updateManager.GetLatestUpdateNotesAsync();
            UpdateSections = updateNotes.Sections;
        }
        catch (Exception)
        {
            UpdateSections = [];
        }
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
            var account = await _dialogService.ShowWinoAccountLoginDialogAsync().ConfigureAwait(false);
            if (account == null)
            {
                return;
            }

            await ExecuteUIThread(() => IsImportInProgress = true);

            var result = await _syncService.ImportAsync(new WinoAccountSyncSelection()).ConfigureAwait(false);
            if (result.ImportedMailboxCount > 0)
            {
                ReportUIChange(new WelcomeImportCompletedMessage(result.ImportedMailboxCount));
                return;
            }

            await ExecuteUIThread(() => ImportStatusMessage = BuildInlineImportMessage(result));
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync(ex.Message, Translator.GeneralTitle_Error, WinoCustomMessageDialogIcon.Error);
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
            var fileContent = await _dialogService.PickWindowsFileContentAsync(".json");
            if (fileContent.Length == 0)
            {
                return;
            }

            await ExecuteUIThread(() => IsImportInProgress = true);

            var jsonContent = Encoding.UTF8.GetString(fileContent);
            var result = await _syncService.ImportFromJsonAsync(jsonContent);
            if (result.ImportedMailboxCount > 0)
            {
                ReportUIChange(new WelcomeImportCompletedMessage(result.ImportedMailboxCount));
                return;
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
            await _dialogService.ShowMessageAsync(ex.Message, Translator.GeneralTitle_Error, WinoCustomMessageDialogIcon.Error);
        }
        finally
        {
            await ExecuteUIThread(() => IsImportInProgress = false);
        }
    }

    private bool CanOpenWelcomeActions() => !IsImportInProgress;

    private static string BuildInlineImportMessage(WinoAccountSyncImportResult result)
    {
        var preferencesMessage = result.FailedPreferenceCount > 0
            ? string.Format(Translator.WinoAccount_Management_ImportPartial, result.AppliedPreferenceCount, result.FailedPreferenceCount)
            : result.HadRemotePreferences
                ? string.Format(Translator.WinoAccount_Management_ImportPreferencesSucceeded, result.AppliedPreferenceCount)
                : string.Empty;

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
