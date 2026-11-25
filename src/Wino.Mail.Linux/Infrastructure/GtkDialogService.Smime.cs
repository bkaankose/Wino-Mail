using Wino.Core.Domain;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class GtkDialogService
{
#if DEBUG
    internal static FormDialog? DebugSmimePassword { get; private set; }
#endif

    /// <summary>
    /// The password of a PKCS#12 file (macOS secure field); null when cancelled. The shared text input would show the
    /// password in clear. The text is never logged or kept.
    /// </summary>
    public Task<string?> ShowSmimePasswordDialogAsync(string fileName)
        => PresentAsync(async owner =>
        {
            var form = new FormDialog(Translator.Smime_CertificatePassword_Title, string.Format(Translator.Smime_CertificatePassword_Placeholder, fileName),
                Translator.Buttons_OK);
            var group = form.AddGroup();
            var password = WinoSettingsRows.Password(Translator.Smime_CertificatePassword_Title);
            group.Add(password);
            form.SetInitialFocus(password);
#if DEBUG
            DebugSmimePassword = form;
#endif
            var confirmed = await form.PresentAsync(owner);
#if DEBUG
            DebugSmimePassword = null;
#endif
            var text = password.GetText();
            password.SetText(string.Empty);
            return confirmed ? text : null;
        }, (string?)null);
}
