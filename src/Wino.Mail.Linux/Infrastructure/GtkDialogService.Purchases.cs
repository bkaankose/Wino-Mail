using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Purchases on Linux go through the Wino Account checkout only: the API returns the checkout address, so there is no
/// store and no channel list. Shared code skips this dialog while neither store exists; it stays correct for any caller.
/// </summary>
internal sealed partial class GtkDialogService
{
    private const string PurchaseContinue = "continue";

    public async Task<UnlimitedAccountsPurchaseChannel?> ShowUnlimitedAccountsPurchaseChannelDialogAsync()
    {
        var signedIn = false;
        try
        {
            if (services.GetService<IWinoAccountProfileService>() is { } profile)
                signedIn = await profile.HasSignedInAccountAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // An unreadable profile counts as signed out, as on macOS: the option stays disabled with its sign-in hint.
            Serilog.Log.Warning(exception, "The Wino Account state could not be read for the purchase channel dialog.");
        }

        var option = signedIn
            ? Translator.UnlimitedAccountsPurchaseDialog_WinoAccountDescription
            : Translator.UnlimitedAccountsPurchaseDialog_WinoAccountSignInRequired;
        var body = $"{Translator.UnlimitedAccountsPurchaseDialog_Description}\n\n{Translator.UnlimitedAccountsPurchaseDialog_WinoAccountTitle}\n{option}";
        var response = await AlertAsync(new AlertRequest(Translator.UnlimitedAccountsPurchaseDialog_Title, body,
            [
                new AlertResponse(Cancel, Translator.Buttons_Cancel),
                new AlertResponse(PurchaseContinue, Translator.Buttons_Continue, Adw.ResponseAppearance.Suggested, Enabled: signedIn),
            ])
        {
            CloseResponse = Cancel,
            DefaultResponse = signedIn ? PurchaseContinue : Cancel,
        });
        return response == PurchaseContinue ? UnlimitedAccountsPurchaseChannel.WinoAccount : null;
    }
}
