#nullable enable
using System;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.ViewModels;

public enum WinoIntelligenceCheckoutOutcome
{
    Cancelled,
    Failed,
    AlreadySubscribed,
    CheckoutOpened
}

/// <summary>
/// The one-button purchase flow for Wino Intelligence: signs in through the Wino Account dialog when needed,
/// stops when the account already has the add-on, otherwise opens Stripe Checkout.
/// </summary>
public static class WinoIntelligenceCheckout
{
    /// <summary>
    /// Returns the signed-in Wino Account, showing the sign-in dialog when there is none.
    /// Must be called on the UI thread because it may present a dialog.
    /// </summary>
    public static async Task<WinoAccount?> EnsureSignedInAsync(IWinoAccountProfileService profileService, IMailDialogService dialogService)
    {
        WinoAccount? account;
        try
        {
            account = await profileService.GetAuthenticatedAccountAsync();
        }
        catch (WinoAccountApiException ex)
        {
            dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                         WinoAccountApiErrorTranslator.Translate(ex.ErrorCode),
                                         InfoBarMessageType.Error);
            return null;
        }

        return account ?? await dialogService.ShowWinoAccountLoginDialogAsync();
    }

    /// <summary>Runs the flow. Must be called on the UI thread because it may present the sign-in dialog.</summary>
    public static async Task<WinoIntelligenceCheckoutOutcome> StartAsync(IWinoAccountProfileService profileService,
                                                                         IMailDialogService dialogService,
                                                                         IWinoBillingService billingService,
                                                                         IWinoAccountIntelligenceSnapshotService entitlementService)
    {
        var account = await EnsureSignedInAsync(profileService, dialogService);
        if (account == null)
            return WinoIntelligenceCheckoutOutcome.Cancelled;

        try
        {
            var entitlement = await entitlementService.RefreshEntitlementAsync().ConfigureAwait(false);
            if (entitlement.CanAccessSurfaces)
                return WinoIntelligenceCheckoutOutcome.AlreadySubscribed;

            if (await billingService.OpenCheckoutAsync(WinoAddOnProductType.AI_PACK).ConfigureAwait(false))
                return WinoIntelligenceCheckoutOutcome.CheckoutOpened;
        }
        catch (OperationCanceledException)
        {
            return WinoIntelligenceCheckoutOutcome.Cancelled;
        }
        catch (Exception)
        {
        }

        dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                     Translator.WinoAccount_Management_PurchaseStartFailed,
                                     InfoBarMessageType.Error);
        return WinoIntelligenceCheckoutOutcome.Failed;
    }
}
