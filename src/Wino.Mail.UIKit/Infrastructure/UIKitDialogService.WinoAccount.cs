using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.UIKit.Views.Account;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>
/// Wino Account sheets: sign-in (with forgot password and the email confirmation follow-up), registration, the sync
/// secret, server certificate trust and the purchase channel (the Mac AppKitDialogService.WinoAccount). Each sheet is
/// presented from the top-most controller, so a certificate prompt during IMAP setup stacks on the progress sheet.
/// </summary>
public sealed partial class UIKitDialogService
{
    /// <summary>Fails closed: Cancel, swiping the sheet away or no window answers false.</summary>
    public Task<bool> ShowServerCertificateTrustDialogAsync(string summary, byte[] certificateRawData)
        => PresentAsync(presenter => new ServerCertificateTrustSheet(summary, certificateRawData).PresentAsync(presenter), false);

    /// <summary>The secret goes straight back to the caller; nothing here logs or keeps it. Null when cancelled.</summary>
    public Task<string?> ShowWinoAccountSyncSecretDialogAsync(SyncSnapshotSecretRequest request)
        => PresentAsync(presenter => new SyncSecretSheet(request).PresentAsync(presenter), null);

    public async Task<UnlimitedAccountsPurchaseChannel?> ShowUnlimitedAccountsPurchaseChannelDialogAsync()
    {
        var capabilities = services.GetService<IPlatformCapabilities>();
        var isWinoAccountAvailable = false;
        try
        {
            if (services.GetService<IWinoAccountProfileService>() is { } profile)
                isWinoAccountAvailable = await profile.HasActiveAccountAsync();
        }
        catch (Exception exception)
        {
            // An unreadable profile leaves the Wino Account option disabled with its sign-in hint.
            Serilog.Log.Warning(exception, "Could not read the Wino Account profile for the purchase channel sheet.");
        }

        return await PresentAsync(presenter => new PurchaseChannelSheet(isWinoAccountAvailable,
            capabilities?.MicrosoftStore == true, capabilities?.AppleAppStore == true).PresentAsync(presenter), null);
    }

    /// <summary>
    /// Same flow as the Mac and Windows: the registration sheet, then "confirmation email sent" for the address. The
    /// account is not signed in until the address is confirmed, so the result is always null.
    /// </summary>
    public async Task<WinoAccount?> ShowWinoAccountRegistrationDialogAsync()
    {
        if (Profile() is not { } profile) return null;
        var email = await PresentAsync(presenter => new WinoAccountRegistrationSheet(profile).PresentAsync(presenter), null);
        if (!string.IsNullOrWhiteSpace(email))
            await ShowMessageAsync(string.Format(Translator.WinoAccount_EmailConfirmationSentDialog_Message, email), Translator.WinoAccount_EmailConfirmationSentDialog_Title, WinoCustomMessageDialogIcon.Information);
        return null;
    }

    /// <summary>Sign-in sheet, then the confirmation sheet or the reset-sent alert when the sheet asks for it.</summary>
    public async Task<WinoAccount?> ShowWinoAccountLoginDialogAsync()
    {
        if (Profile() is not { } profile) return null;
        var outcome = await PresentAsync(presenter => new WinoAccountLoginSheet(profile).PresentAsync(presenter), null);
        if (outcome is { ConfirmationDetails: { } details, PendingConfirmationEmail: { Length: > 0 } pending })
        {
            if (await PresentAsync(presenter => new WinoAccountEmailConfirmationSheet(profile, pending, details).PresentAsync(presenter), false))
                await ShowMessageAsync(string.Format(Translator.WinoAccount_EmailConfirmationResentDialog_Message, pending), Translator.WinoAccount_EmailConfirmationResentDialog_Title, WinoCustomMessageDialogIcon.Information);
            return null;
        }
        if (outcome is { PasswordResetEmail: { Length: > 0 } reset })
        {
            await ShowMessageAsync(string.Format(Translator.WinoAccount_ForgotPasswordDialog_SuccessMessage, reset), Translator.WinoAccount_ForgotPasswordDialog_SuccessTitle, WinoCustomMessageDialogIcon.Information);
            return null;
        }
        return outcome?.Account;
    }

    private IWinoAccountProfileService? Profile()
    {
        var profile = services.GetService<IWinoAccountProfileService>();
        if (profile is null) Serilog.Log.Warning("The Wino Account profile service is not registered; the Wino Account sheet cannot open.");
        return profile;
    }
}
