using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;
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
    }

    public WinoAccountSyncExportResult? Result { get; private set; }

    public Exception? FailureException { get; private set; }

    private async void ExportClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;

        if (_isBusy)
        {
            return;
        }

        var deferral = args.GetDeferral();

        try
        {
            SetBusyState(true);
            FailureException = null;
            Result = await _syncService.ExportAsync(new WinoAccountSyncSelection(), PromptSecretAsync);
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

    // Every backup asks for its own password. The dialog hides while the prompt is shown.
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
        IsPrimaryButtonEnabled = !isBusy;
        IsSecondaryButtonEnabled = !isBusy;
    }
}
