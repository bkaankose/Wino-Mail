using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Accounts;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Account;

/// <summary>
/// Asks for the password that unlocks a sync snapshot, or has the user choose and confirm one for a
/// new backup (port of WinoAccountSyncSecretDialog). The value is handed back to the caller only:
/// it is never logged or stored, and the fields are cleared when the sheet closes.
/// </summary>
internal sealed class SyncSecretSheet : WinoAccountSheet<string>
{
    private readonly bool _isNewBackup;
    private readonly NSTextField _secret;
    private readonly NSTextField _confirm;
    private readonly NSTextField _error;

    public SyncSecretSheet(SyncSnapshotSecretRequest request) : base(480)
    {
        _isNewBackup = request.IsNewBackup;

        var title = _isNewBackup ? Translator.WinoAccount_Sync_SecretDialog_NewTitle : Translator.WinoAccount_Sync_SecretDialog_Title;
        var description = _isNewBackup
            ? Translator.WinoAccount_Sync_SecretDialog_NewDescription_Device
            : request.IsPassphrase ? Translator.WinoAccount_Sync_SecretDialog_PassphraseDescription : Translator.WinoAccount_Sync_SecretDialog_PasswordDescription;
        var secretLabel = _isNewBackup || request.IsPassphrase
            ? Translator.WinoAccount_Sync_SecretDialog_PassphraseLabel
            : Translator.WinoAccount_Sync_SecretDialog_PasswordLabel;

        Sheet.Title = title;
        var heading = WinoStyle.Label(title, NSFont.SystemFontOfSize(18, NSFontWeight.Semibold), WinoStyle.PrimaryText, 0);
        heading.AccessibilityRole = NSAccessibilityRoles.StaticTextRole;
        var text = WinoStyle.Label(description, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        text.PreferredMaxLayoutWidth = 432;
        var header = WinoLayout.VStack(8, heading, text);
        header.Alignment = NSLayoutAttribute.Leading;
        text.WidthAnchor.ConstraintEqualTo(header.WidthAnchor).Active = true;
        AddRow(header);

        var secretRow = Field(secretLabel, out _secret, secure: true);
        var confirmRow = Field(Translator.WinoAccount_Sync_SecretDialog_ConfirmLabel, out _confirm, secure: true);
        // A new backup password is chosen here, so only the unlock field offers saved-password autofill.
        if (!_isNewBackup) _secret.ContentType = NSTextContentType.Password;
        confirmRow.Hidden = !_isNewBackup;
        _error = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(12), WinoStyle.Critical, 0);
        _error.PreferredMaxLayoutWidth = 432;
        _error.Hidden = true;
        _error.AccessibilityRole = NSAccessibilityRoles.StaticTextRole;

        var fields = WinoLayout.VStack(12, secretRow, confirmRow, _error);
        fields.Alignment = NSLayoutAttribute.Leading;
        foreach (var view in new NSView[] { secretRow, confirmRow, _error }) view.WidthAnchor.ConstraintEqualTo(fields.WidthAnchor).Active = true;
        AddRow(fields);

        Secondary.Title = Translator.Buttons_Cancel;
        Primary.Title = _isNewBackup ? Translator.Buttons_Continue : Translator.Buttons_Unlock;

        _secret.Changed += (_, _) => SecretChanged();
        _confirm.Changed += (_, _) => SecretChanged();
        // Return in the first field of a new backup moves to the confirmation (the default button is
        // still disabled then, so the field receives its own action).
        _secret.Activated += (_, _) => { if (_isNewBackup && string.IsNullOrEmpty(_confirm.StringValue)) Sheet.MakeFirstResponder(_confirm); };

        // Shown with the sheet; Opened() announces it once the sheet is on screen.
        if (request.WasRejected)
        {
            _error.StringValue = Translator.WinoAccount_Sync_SecretDialog_Rejected;
            _error.Hidden = false;
        }
        Primary.Enabled = false;
    }

    protected override NSView? InitialResponder => _secret;
    protected override IEnumerable<NSTextField> Fields => [_secret, _confirm];
    protected override bool CanSubmit => !string.IsNullOrEmpty(_secret.StringValue) && (!_isNewBackup || !string.IsNullOrEmpty(_confirm.StringValue));

    private void SecretChanged()
    {
        Primary.Enabled = !IsBusy && CanSubmit;
        if (_error.Hidden) return;
        _error.Hidden = true;
        Resize();
    }

    protected override Task PrimaryAsync()
    {
        // Windows: Return in the first field of a new backup moves on instead of submitting.
        if (_isNewBackup && IsEditing(_secret) && string.IsNullOrEmpty(_confirm.StringValue))
        {
            Sheet.MakeFirstResponder(_confirm);
            return Task.CompletedTask;
        }

        if (string.IsNullOrEmpty(_secret.StringValue)) { ShowError(Translator.WinoAccount_Sync_SecretDialog_Required); return Task.CompletedTask; }
        if (_isNewBackup && _secret.StringValue != _confirm.StringValue)
        {
            ShowError(Translator.WinoAccount_Sync_SecretDialog_Mismatch);
            Sheet.MakeFirstResponder(_confirm);
            return Task.CompletedTask;
        }

        Finish(_secret.StringValue);
        return Task.CompletedTask;
    }

    private void ShowError(string message)
    {
        _error.StringValue = message;
        _error.Hidden = false;
        Resize();
        Announce(message);
    }

    protected override void Opened()
    {
        if (!_error.Hidden) Announce(_error.StringValue);
    }

    private void Announce(string message)
        => NSAccessibility.PostNotification(Sheet, new NSString("AXAnnouncementRequested"),
            NSDictionary.FromObjectAndKey(new NSString(message), NSAccessibilityNotificationUserInfoKeys.AnnouncementKey));

    protected override void Closed()
    {
        // The secret only lives in the returned string; the fields forget it.
        _secret.StringValue = string.Empty;
        _confirm.StringValue = string.Empty;
    }
}
