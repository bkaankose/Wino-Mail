using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Dialogs;

/// <summary>
/// Asks for the password that unlocks a sync snapshot, or has the user choose and confirm one for
/// a new backup. The value is handed back to the caller and never stored.
/// </summary>
public sealed partial class WinoAccountSyncSecretDialog : ContentDialog
{
    private readonly bool _isNewBackup;

    public WinoAccountSyncSecretDialog(SyncSnapshotSecretRequest request)
    {
        InitializeComponent();

        _isNewBackup = request.IsNewBackup;

        // Set from code only: x:Bind values in the XAML would overwrite these when the dialog loads.
        Title = _isNewBackup ? Translator.WinoAccount_Sync_SecretDialog_NewTitle : Translator.WinoAccount_Sync_SecretDialog_Title;
        PrimaryButtonText = _isNewBackup ? Translator.Buttons_Continue : Translator.Buttons_Unlock;

        if (_isNewBackup)
        {
            DescriptionTextBlock.Text = Translator.WinoAccount_Sync_SecretDialog_NewDescription_Device;
            SecretBox.Header = Translator.WinoAccount_Sync_SecretDialog_PassphraseLabel;
            ConfirmBox.Header = Translator.WinoAccount_Sync_SecretDialog_ConfirmLabel;
            ConfirmBox.Visibility = Visibility.Visible;
        }
        else
        {
            DescriptionTextBlock.Text = request.IsPassphrase
                ? Translator.WinoAccount_Sync_SecretDialog_PassphraseDescription
                : Translator.WinoAccount_Sync_SecretDialog_PasswordDescription;
            SecretBox.Header = request.IsPassphrase
                ? Translator.WinoAccount_Sync_SecretDialog_PassphraseLabel
                : Translator.WinoAccount_Sync_SecretDialog_PasswordLabel;
        }

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

        if (_isNewBackup && ReferenceEquals(sender, SecretBox))
        {
            ConfirmBox.Focus(FocusState.Keyboard);
            return;
        }

        if (TryAccept())
        {
            Hide();
        }
    }

    private void SecretChanged(object sender, RoutedEventArgs e)
    {
        IsPrimaryButtonEnabled = !string.IsNullOrEmpty(SecretBox.Password)
            && (!_isNewBackup || !string.IsNullOrEmpty(ConfirmBox.Password));
        ErrorTextBlock.Visibility = Visibility.Collapsed;
    }

    private bool TryAccept()
    {
        if (string.IsNullOrEmpty(SecretBox.Password))
        {
            ShowError(Translator.WinoAccount_Sync_SecretDialog_Required);
            return false;
        }

        if (_isNewBackup && SecretBox.Password != ConfirmBox.Password)
        {
            ShowError(Translator.WinoAccount_Sync_SecretDialog_Mismatch);
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
