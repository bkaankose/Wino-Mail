#nullable enable
using System;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.ViewModels;

/// <summary>
/// The Mac App Store channel for Wino add-ons, the counterpart of <see cref="UnlimitedAccountsStorePurchase"/>.
/// Every page that sells an add-on through the App Store uses it, so the outcome is reported the same way.
/// </summary>
public static class AppStoreAddOnPurchase
{
    /// <summary>Buys the add-on and reports the outcome. Returns true when the caller should refresh.</summary>
    public static async Task<bool> PurchaseAsync(IWinoAppStorePurchaseService appStorePurchases, WinoAddOnProductType productType,
        IDialogServiceBase dialogService, IWinoLogger? logger)
    {
        if (!appStorePurchases.IsAvailable)
            return false;

        WinoAppStorePurchaseOutcome outcome;
        try
        {
            outcome = await appStorePurchases.PurchaseAsync(productType).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.CaptureException(ex, nameof(AppStoreAddOnPurchase));
            outcome = WinoAppStorePurchaseOutcome.Failed;
        }

        switch (outcome)
        {
            case WinoAppStorePurchaseOutcome.Purchased:
                dialogService.InfoBarMessage(Translator.Info_PurchaseThankYouTitle, Translator.Info_PurchaseThankYouMessage, InfoBarMessageType.Success);
                return true;
            case WinoAppStorePurchaseOutcome.PurchasedLinkPending:
                dialogService.InfoBarMessage(Translator.Info_PurchaseThankYouTitle, Translator.AppStorePurchase_LinkPending, InfoBarMessageType.Success);
                return true;
            case WinoAppStorePurchaseOutcome.Pending:
                dialogService.InfoBarMessage(Translator.GeneralTitle_Info, Translator.AppStorePurchase_Pending, InfoBarMessageType.Information);
                return false;
            case WinoAppStorePurchaseOutcome.SignInRequired:
                dialogService.InfoBarMessage(Translator.GeneralTitle_Warning, Translator.WinoAccount_Management_CheckoutSignInRequired, InfoBarMessageType.Warning);
                return false;
            case WinoAppStorePurchaseOutcome.Failed:
                dialogService.InfoBarMessage(Translator.GeneralTitle_Error, Translator.AppStorePurchase_Failed, InfoBarMessageType.Error);
                return false;
            default:
                return false;
        }
    }
}
