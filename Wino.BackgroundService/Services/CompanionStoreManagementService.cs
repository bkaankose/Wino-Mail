using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Services.Store;
using Wino.Core.Domain.Interfaces;
using WinoAddOnProductType = Wino.Core.Domain.Enums.WinoAddOnProductType;
using WinoStorePurchaseResult = Wino.Core.Domain.Enums.StorePurchaseResult;

namespace Wino.BackgroundService.Services;

/// <summary>
/// Store access for the headless companion. License and customer-id queries work without
/// a window; purchase requests require window association and therefore fail gracefully
/// here — purchases must be initiated while the UI process drives the flow.
/// </summary>
public sealed class CompanionStoreManagementService : IStoreManagementService
{
    private StoreContext CurrentContext { get; } = StoreContext.GetDefault();

    private readonly Dictionary<WinoAddOnProductType, string> productIds = new()
    {
        { WinoAddOnProductType.UNLIMITED_ACCOUNTS, "UnlimitedAccounts" },
        { WinoAddOnProductType.AI_PACK, "AI_PACK" },
    };

    public async Task<bool> HasProductAsync(WinoAddOnProductType productType)
    {
        if (!productIds.TryGetValue(productType, out var productKey))
            return false;

        var appLicense = await CurrentContext.GetAppLicenseAsync();

        if (appLicense == null)
            return false;

        foreach (KeyValuePair<string, StoreLicense> item in appLicense.AddOnLicenses)
        {
            var addOnLicense = item.Value;

            if (addOnLicense.InAppOfferToken == productKey)
            {
                return addOnLicense.IsActive;
            }
        }

        return false;
    }

    public async Task<WinoStorePurchaseResult> PurchaseAsync(WinoAddOnProductType productType)
    {
        // Purchase UI cannot be shown from a windowless process; report already-purchased
        // when the license exists, otherwise fail so the UI process can drive the purchase.
        return await HasProductAsync(productType)
            ? WinoStorePurchaseResult.AlreadyPurchased
            : WinoStorePurchaseResult.NotPurchased;
    }

    public async Task<string?> GetCustomerCollectionsIdAsync(string serviceTicket, string publisherUserId)
    {
        if (string.IsNullOrWhiteSpace(serviceTicket) || string.IsNullOrWhiteSpace(publisherUserId))
            return null;

        var collectionsId = await CurrentContext.GetCustomerCollectionsIdAsync(serviceTicket, publisherUserId);
        return string.IsNullOrWhiteSpace(collectionsId) ? null : collectionsId;
    }

    public async Task<string?> GetCustomerPurchaseIdAsync(string serviceTicket, string publisherUserId)
    {
        if (string.IsNullOrWhiteSpace(serviceTicket) || string.IsNullOrWhiteSpace(publisherUserId))
            return null;

        var purchaseId = await CurrentContext.GetCustomerPurchaseIdAsync(serviceTicket, publisherUserId);
        return string.IsNullOrWhiteSpace(purchaseId) ? null : purchaseId;
    }
}
