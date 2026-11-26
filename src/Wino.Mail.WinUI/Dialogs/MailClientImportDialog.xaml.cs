using System;
using System.Threading;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.WinUI.Dialogs;

/// <summary>Scans the other mail clients when it opens; closing it stops a scan that is still running.</summary>
public sealed partial class MailClientImportDialog : ContentDialog
{
    private readonly CancellationTokenSource _scanCancellation = new();

    internal MailClientImportDialogModel Model { get; }

    public MailClientImportDialog(MailClientImportDialogModel model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));

        InitializeComponent();
    }

    private async void DialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        => await Model.ScanAsync(_scanCancellation.Token);

    private void DialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        => _scanCancellation.Cancel();
}
