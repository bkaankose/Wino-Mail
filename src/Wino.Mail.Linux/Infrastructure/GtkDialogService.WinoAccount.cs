using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Account;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>Wino Account sign-in, registration and export (macOS AppKitDialogService orchestration).</summary>
internal sealed partial class GtkDialogService
{
    public async Task<WinoAccount?> ShowWinoAccountRegistrationDialogAsync()
    {
        var profile = services.GetRequiredService<IWinoAccountProfileService>();
        var launcher = services.GetRequiredService<IExternalLauncher>();
        var email = await PresentAsync(owner => new WinoAccountRegistrationDialog(profile, launcher).PresentAsync(owner), null);
        if (email is not null)
            await ShowMessageAsync(string.Format(Translator.WinoAccount_EmailConfirmationSentDialog_Message, email), Translator.WinoAccount_EmailConfirmationSentDialog_Title, WinoCustomMessageDialogIcon.Information);
        // The account exists only after the e-mail is confirmed and the user signs in.
        return null;
    }

    public async Task<WinoAccount?> ShowWinoAccountLoginDialogAsync()
    {
        var profile = services.GetRequiredService<IWinoAccountProfileService>();
        var externalSignIn = services.GetService<IWinoAccountExternalSignInService>();
        var outcome = await PresentAsync(owner => new WinoAccountLoginDialog(profile, externalSignIn).PresentAsync(owner), null);
        if (outcome is { PendingConfirmationEmail: { } pending, ConfirmationDetails: { } details })
        {
            if (await PresentAsync(owner => new WinoAccountEmailConfirmationDialog(profile, pending, details).PresentAsync(owner), false))
                await ShowMessageAsync(string.Format(Translator.WinoAccount_EmailConfirmationResentDialog_Message, pending), Translator.WinoAccount_EmailConfirmationResentDialog_Title, WinoCustomMessageDialogIcon.Information);
            return null;
        }
        if (outcome is { PasswordResetEmail: { } reset })
        {
            await ShowMessageAsync(string.Format(Translator.WinoAccount_ForgotPasswordDialog_SuccessMessage, reset), Translator.WinoAccount_ForgotPasswordDialog_SuccessTitle, WinoCustomMessageDialogIcon.Information);
            return null;
        }
        return outcome?.Account;
    }

    public Task<bool> ShowWinoAccountExportDialogAsync() => ShowConfirmationDialogAsync(
        $"{Translator.WinoAccount_Management_ExportDialog_Confirmation}\n\n{Translator.WinoAccount_Management_ExportDialog_EncryptionNotice}\n\n{Translator.WinoAccount_Management_ExportDialog_AccountsDisclaimer} {Translator.WinoAccount_Management_ExportDialog_AccountsRelogin_Device}",
        Translator.WinoAccount_Management_ExportDialog_Title, Translator.WinoAccount_Management_LocalDataExportAction);
}
