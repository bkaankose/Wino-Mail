using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Services.Store;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Store;
using WinoStorePurchaseResult = Wino.Core.Domain.Enums.StorePurchaseResult;

namespace Wino.Mail.WinUI.Services;

public class StoreManagementService : IStoreManagementService
{
    private StoreContext CurrentContext { get; }

    private readonly Dictionary<StoreProductType, string> productIds = new Dictionary<StoreProductType, string>()
    {
        { StoreProductType.UnlimitedAccounts, "UnlimitedAccounts" }
    };

    private readonly Dictionary<StoreProductType, string> skuIds = new Dictionary<StoreProductType, string>()
    {
        { StoreProductType.UnlimitedAccounts, "9P02MXZ42GSM" }
    };

    public StoreManagementService()
    {
        CurrentContext = StoreContext.GetDefault();
    }

    public async Task<bool> HasProductAsync(StoreProductType productType)
    {
        var productKey = productIds[productType];
        var appLicense = await CurrentContext.GetAppLicenseAsync();

        if (appLicense == null)
            return false;

        // Access the valid licenses for durable add-ons for this app.
        foreach (KeyValuePair<string, StoreLicense> item in appLicense.AddOnLicenses)
        {
            StoreLicense addOnLicense = item.Value;

            if (addOnLicense.InAppOfferToken == productKey)
            {
                return addOnLicense.IsActive;
            }
        }

        return false;
    }

    public async Task<WinoStorePurchaseResult> PurchaseAsync(StoreProductType productType)
    {
        if (await HasProductAsync(productType))
            return WinoStorePurchaseResult.AlreadyPurchased;
        else
        {
            var productKey = skuIds[productType];

            var result = await CurrentContext.RequestPurchaseAsync(productKey);

            switch (result.Status)
            {
                case StorePurchaseStatus.Succeeded:
                    return WinoStorePurchaseResult.Succeeded;
                case StorePurchaseStatus.AlreadyPurchased:
                    return WinoStorePurchaseResult.AlreadyPurchased;
                default:
                    return WinoStorePurchaseResult.NotPurchased;
            }
        }
    }
}
