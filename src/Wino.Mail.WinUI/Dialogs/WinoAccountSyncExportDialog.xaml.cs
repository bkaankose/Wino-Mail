using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Dialogs;

public sealed partial class WinoAccountSyncExportDialog : ContentDialog
{
    private readonly IWinoAccountDataSyncService _syncService;
    private bool _isBusy;

    private readonly IMailDialogService _dialogService;

    public WinoAccountSyncExportDialog(IWinoAccountDataSyncService syncService, IMailDialogService dialogService)
    {
        _syncService = syncService;
        _dialogService = dialogService;
        InitializeComponent();
        UpdateButtonState();
    }

    public WinoAccountSyncExportResult? Result { get; private set; }

    public Exception? FailureException { get; private set; }

    private async void ExportClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;

        if (!HasSelection())
        {
            return;
        }

        var deferral = args.GetDeferral();

        try
        {
            SetBusyState(true);
            FailureException = null;
            Result = await _syncService.ExportAsync(new WinoAccountSyncSelection(
                PreferencesCheckBox.IsChecked == true,
                AccountsCheckBox.IsChecked == true), PromptSecretAsync);
            Hide();
        }
        catch (Exception ex)
        {
            FailureException = ex;
            Hide();
        }
        finally
        {
            SetBusyState(false);
            deferral.Complete();
        }
    }

    private void SelectionChanged(object sender, RoutedEventArgs e)
        => UpdateButtonState();

    // The key is normally cached at sign-in. Only an account that signed in before this build,
    // or one without a password, has to answer here. The dialog hides while the prompt is shown.
    private async System.Threading.Tasks.Task<string?> PromptSecretAsync(SyncSnapshotSecretRequest request)
    {
        Hide();

        var secret = await _dialogService.ShowWinoAccountSyncSecretDialogAsync(request);
        if (secret != null)
        {
            _ = ShowAsync();
        }

        return secret;
    }

    private void SetBusyState(bool isBusy)
    {
        _isBusy = isBusy;
        ProgressPanel.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        IsSecondaryButtonEnabled = !isBusy;
        UpdateButtonState();
    }

    private void UpdateButtonState()
        => IsPrimaryButtonEnabled = !_isBusy && HasSelection();

    private bool HasSelection()
        => PreferencesCheckBox.IsChecked == true || AccountsCheckBox.IsChecked == true;
}
