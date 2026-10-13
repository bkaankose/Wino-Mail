using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Accounts;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Account;

/// <summary>
/// Asks for the password that unlocks a sync snapshot, or has the user choose and confirm one for a new backup (port
/// of WinoAccountSyncSecretDialog). The value is handed back to the caller only: it is never logged or stored, and the
/// fields are cleared when the sheet closes.
/// </summary>
internal sealed class SyncSecretSheet : WinoAccountSheet<string>
{
    private readonly bool _isNewBackup;
    private readonly UITextField _secret;
    private readonly UITextField _confirm;
    private readonly string? _rejected;

    public SyncSecretSheet(SyncSnapshotSecretRequest request)
        : base(request.IsNewBackup ? Translator.WinoAccount_Sync_SecretDialog_NewTitle : Translator.WinoAccount_Sync_SecretDialog_Title)
    {
        _isNewBackup = request.IsNewBackup;
        var description = _isNewBackup
            ? Translator.WinoAccount_Sync_SecretDialog_NewDescription_Device
            : request.IsPassphrase ? Translator.WinoAccount_Sync_SecretDialog_PassphraseDescription : Translator.WinoAccount_Sync_SecretDialog_PasswordDescription;
        var secretLabel = _isNewBackup || request.IsPassphrase
            ? Translator.WinoAccount_Sync_SecretDialog_PassphraseLabel
            : Translator.WinoAccount_Sync_SecretDialog_PasswordLabel;

        AddRow(WinoStyle.Label(description, WinoStyle.Body, WinoStyle.SecondaryText, 0));

        var secretRow = Field(secretLabel, out _secret, secure: true);
        var confirmRow = Field(Translator.WinoAccount_Sync_SecretDialog_ConfirmLabel, out _confirm, secure: true);
        // A new backup password is chosen here, so only the unlock field offers saved-password autofill.
        _secret.TextContentType = _isNewBackup ? UITextContentType.NewPassword : UITextContentType.Password;
        _confirm.TextContentType = UITextContentType.NewPassword;
        confirmRow.Hidden = !_isNewBackup;
        var fields = WinoLayout.VStack(12, secretRow, confirmRow);
        fields.Alignment = UIStackViewAlignment.Fill;
        AddRow(fields);

        Primary.Title = _isNewBackup ? Translator.Buttons_Continue : Translator.Buttons_Unlock;
        if (request.WasRejected)
        {
            _rejected = Translator.WinoAccount_Sync_SecretDialog_Rejected;
            ShowError(_rejected);
        }
    }

    protected override bool CanSubmit => !string.IsNullOrEmpty(_secret.Text) && (!_isNewBackup || !string.IsNullOrEmpty(_confirm.Text));

    protected override Task PrimaryAsync()
    {
        var secret = _secret.Text ?? string.Empty;
        if (secret.Length == 0) { ShowError(Translator.WinoAccount_Sync_SecretDialog_Required); return Task.CompletedTask; }
        if (_isNewBackup && secret != (_confirm.Text ?? string.Empty))
        {
            ShowError(Translator.WinoAccount_Sync_SecretDialog_Mismatch);
            _confirm.BecomeFirstResponder();
            return Task.CompletedTask;
        }
        Finish(secret);
        return Task.CompletedTask;
    }

    protected override void Opened()
    {
        // The rejected-password line is shown with the sheet; announce it once the sheet is on screen.
        if (_rejected is not null) UIAccessibility.PostNotification(UIAccessibilityPostNotification.Announcement, new Foundation.NSString(_rejected));
    }

    protected override void Closed()
    {
        // The secret only lives in the returned string; the fields forget it.
        _secret.Text = string.Empty;
        _confirm.Text = string.Empty;
    }
}
