#nullable enable
using System;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.ViewModels;

/// <summary>
/// The Microsoft Store channel for Unlimited Accounts. Every page that sells the add-on uses it,
/// so the Store purchase works without a Wino Account and reports the same outcome everywhere.
/// </summary>
public static class UnlimitedAccountsStorePurchase
{
    /// <summary>
    /// Requests the Store purchase and reports the outcome.
    /// Returns true when the Store owns the add-on for this device user and the caller should refresh.
    /// </summary>
    public static async Task<bool> PurchaseAsync(IMicrosoftStoreService storeService, IDialogServiceBase dialogService, IWinoLogger? logger)
    {
        try
        {
            var purchaseResult = await storeService
                .PurchaseAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS)
                .ConfigureAwait(false);

            if (purchaseResult == StorePurchaseResult.Succeeded)
            {
                dialogService.InfoBarMessage(
                    Translator.Info_PurchaseThankYouTitle,
                    Translator.Info_PurchaseThankYouMessage,
                    InfoBarMessageType.Success);
                return true;
            }

            if (purchaseResult == StorePurchaseResult.AlreadyPurchased)
            {
                dialogService.InfoBarMessage(
                    Translator.Info_PurchaseExistsTitle,
                    Translator.Info_PurchaseExistsMessage,
                    InfoBarMessageType.Warning);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            logger?.CaptureException(ex, nameof(UnlimitedAccountsStorePurchase));
            dialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                Translator.UnlimitedAccountsPurchaseDialog_MicrosoftStorePurchaseFailed,
                InfoBarMessageType.Error);
            return false;
        }
    }
}
