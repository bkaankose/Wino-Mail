using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Dialogs;

/// <summary>
/// Asks for the secret that unlocks a sync snapshot. The value is handed back to the caller and
/// never stored; the key service keeps only the derived key.
/// </summary>
public sealed partial class WinoAccountSyncSecretDialog : ContentDialog
{
    public WinoAccountSyncSecretDialog(SyncSnapshotSecretRequest request)
    {
        InitializeComponent();

        DescriptionTextBlock.Text = request.IsPassphrase
            ? Translator.WinoAccount_Sync_SecretDialog_PassphraseDescription
            : Translator.WinoAccount_Sync_SecretDialog_PasswordDescription;
        SecretBox.Header = request.IsPassphrase
            ? Translator.WinoAccount_Sync_SecretDialog_PassphraseLabel
            : Translator.WinoAccount_Sync_SecretDialog_PasswordLabel;

        if (request.WasRejected)
        {
            ShowError(Translator.WinoAccount_Sync_SecretDialog_Rejected);
        }

        IsPrimaryButtonEnabled = false;
    }

    public string? Result { get; private set; }

    private void UnlockClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!TryAccept())
        {
            args.Cancel = true;
        }
    }

    private void SecretBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;

        e.Handled = true;

        if (TryAccept())
        {
            Hide();
        }
    }

    private void SecretChanged(object sender, RoutedEventArgs e)
    {
        IsPrimaryButtonEnabled = !string.IsNullOrEmpty(SecretBox.Password);
        ErrorTextBlock.Visibility = Visibility.Collapsed;
    }

    private bool TryAccept()
    {
        if (string.IsNullOrEmpty(SecretBox.Password))
        {
            ShowError(Translator.WinoAccount_Sync_SecretDialog_Required);
            return false;
        }

        Result = SecretBox.Password;
        return true;
    }

    private void ShowError(string message)
    {
        ErrorTextBlock.Text = message;
        ErrorTextBlock.Visibility = Visibility.Visible;
    }
}
