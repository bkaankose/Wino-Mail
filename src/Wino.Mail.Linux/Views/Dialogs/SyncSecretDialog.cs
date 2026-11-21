using Wino.Core.Domain;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// The backup password prompt (macOS SyncSecretSheet, Windows WinoAccountSyncSecretDialog): one field to unlock a
/// backup, two to choose the password of a new one. The value never leaves the returned string.
/// </summary>
internal static class SyncSecretDialog
{
    /// <summary>The password, or null when the user cancelled. Main thread.</summary>
    public static async Task<string?> PresentAsync(Gtk.Widget owner, SyncSnapshotSecretRequest request)
    {
        var isNew = request.IsNewBackup;
        var title = request.Title ?? (isNew ? Translator.WinoAccount_Sync_SecretDialog_NewTitle : Translator.WinoAccount_Sync_SecretDialog_Title);
        var description = request.Description ?? (isNew
            ? Translator.WinoAccount_Sync_SecretDialog_NewDescription_Device
            : request.IsPassphrase ? Translator.WinoAccount_Sync_SecretDialog_PassphraseDescription : Translator.WinoAccount_Sync_SecretDialog_PasswordDescription);
        var label = request.FieldLabel ?? (isNew || request.IsPassphrase
            ? Translator.WinoAccount_Sync_SecretDialog_PassphraseLabel
            : Translator.WinoAccount_Sync_SecretDialog_PasswordLabel);

        var form = new FormDialog(title, description, isNew ? Translator.Buttons_Continue : Translator.Buttons_Unlock);
        var secret = WinoSettingsRows.Password(label);
        var confirm = WinoSettingsRows.Password(request.ConfirmLabel ?? Translator.WinoAccount_Sync_SecretDialog_ConfirmLabel);
        confirm.SetVisible(isNew);
        confirm.SetActivatesDefault(true);
        // A new backup moves Enter from the first field to the confirmation instead of confirming with one field filled.
        secret.SetActivatesDefault(!isNew);
        var group = form.AddGroup();
        group.Add(secret);
        group.Add(confirm);

        form.CanConfirm = () => secret.GetText().Length > 0 && (!isNew || confirm.GetText().Length > 0);
        form.Validate = () =>
        {
            if (secret.GetText().Length == 0) return Translator.WinoAccount_Sync_SecretDialog_Required;
            if (isNew && !string.Equals(secret.GetText(), confirm.GetText(), StringComparison.Ordinal))
            {
                confirm.GrabFocus();
                return Translator.WinoAccount_Sync_SecretDialog_Mismatch;
            }
            return null;
        };
        void Changed()
        {
            form.ShowError(null);
            form.Refresh();
        }
        form.Own(GtkSignals.OnChanged(secret, Changed));
        form.Own(GtkSignals.OnChanged(confirm, Changed));
        if (isNew)
        {
            form.Own(GtkSignals.Connect<Adw.EntryRow>(h => secret.OnEntryActivated += h, h => secret.OnEntryActivated -= h,
                (_, _) => confirm.GrabFocus()));
        }
        if (request.WasRejected) form.ShowError(Translator.WinoAccount_Sync_SecretDialog_Rejected);
        form.SetInitialFocus(secret);

        var confirmed = await form.PresentAsync(owner);
        var value = secret.GetText();
        secret.SetText(string.Empty);
        confirm.SetText(string.Empty);
        return confirmed ? value : null;
    }
}
