using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Services.Store;
using Wino.Core.Domain.Interfaces;
using WinRT.Interop;
using WinoAddOnProductType = Wino.Core.Domain.Enums.WinoAddOnProductType;
using WinoStorePurchaseResult = Wino.Core.Domain.Enums.StorePurchaseResult;

namespace Wino.Mail.WinUI.Services;

public class StoreManagementService : IStoreManagementService
{
    private StoreContext CurrentContext { get; }

    private readonly Dictionary<WinoAddOnProductType, string> productIds = new Dictionary<WinoAddOnProductType, string>()
    {
        { WinoAddOnProductType.UNLIMITED_ACCOUNTS, "UnlimitedAccounts" },
        { WinoAddOnProductType.AI_PACK, "AI_PACK" },
    };

    private readonly Dictionary<WinoAddOnProductType, string> skuIds = new Dictionary<WinoAddOnProductType, string>()
    {
        { WinoAddOnProductType.AI_PACK, "9N2FH734RBVS" },
        { WinoAddOnProductType.UNLIMITED_ACCOUNTS, "9P02MXZ42GSM" }
    };

    public StoreManagementService()
    {
        CurrentContext = StoreContext.GetDefault();
    }

    public async Task<bool> HasProductAsync(WinoAddOnProductType productType)
    {
        if (!productIds.TryGetValue(productType, out var productKey))
            return false;

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

    public async Task<WinoStorePurchaseResult> PurchaseAsync(WinoAddOnProductType productType)
    {
        if (!skuIds.TryGetValue(productType, out var productKey))
            return WinoStorePurchaseResult.NotPurchased;

        if (await HasProductAsync(productType))
            return WinoStorePurchaseResult.AlreadyPurchased;
        else
        {
            InitializeStoreContextWithWindow();
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

    public async Task<string?> GetCustomerCollectionsIdAsync(string serviceTicket, string publisherUserId)
    {
        if (string.IsNullOrWhiteSpace(serviceTicket) || string.IsNullOrWhiteSpace(publisherUserId))
        {
            return null;
        }

        InitializeStoreContextWithWindow();
        var collectionsId = await CurrentContext.GetCustomerCollectionsIdAsync(serviceTicket, publisherUserId);
        return string.IsNullOrWhiteSpace(collectionsId) ? null : collectionsId;
    }

    public async Task<string?> GetCustomerPurchaseIdAsync(string serviceTicket, string publisherUserId)
    {
        if (string.IsNullOrWhiteSpace(serviceTicket) || string.IsNullOrWhiteSpace(publisherUserId))
        {
            return null;
        }

        InitializeStoreContextWithWindow();
        var purchaseId = await CurrentContext.GetCustomerPurchaseIdAsync(serviceTicket, publisherUserId);
        return string.IsNullOrWhiteSpace(purchaseId) ? null : purchaseId;
    }

    private void InitializeStoreContextWithWindow()
    {
        var mainWindow = WinoApplication.MainWindow;
        if (mainWindow == null)
        {
            return;
        }

        InitializeWithWindow.Initialize(CurrentContext, WindowNative.GetWindowHandle(mainWindow));
    }
}
