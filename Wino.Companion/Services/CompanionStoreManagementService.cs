using Windows.Services.Store;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using WinoStorePurchaseResult = Wino.Core.Domain.Enums.StorePurchaseResult;

namespace Wino.Companion.Services;

/// <summary>
/// Provides the package-scoped Store identity data needed by the account backend.
/// Interactive purchases remain in the UWP process so the Store UI has a visible owner.
/// </summary>
internal sealed class CompanionStoreManagementService : IStoreManagementService
{
    private static readonly IReadOnlyDictionary<WinoAddOnProductType, string> ProductIds =
        new Dictionary<WinoAddOnProductType, string>
        {
            [WinoAddOnProductType.UNLIMITED_ACCOUNTS] = "UnlimitedAccounts",
            [WinoAddOnProductType.AI_PACK] = "AI_PACK",
        };

    private readonly StoreContext context = StoreContext.GetDefault();

    public async Task<bool> HasProductAsync(WinoAddOnProductType productType)
    {
        if (!ProductIds.TryGetValue(productType, out var productId))
        {
            return false;
        }

        var license = await context.GetAppLicenseAsync();
        return license?.AddOnLicenses.Values.Any(addOn =>
            addOn.IsActive && string.Equals(addOn.InAppOfferToken, productId, StringComparison.Ordinal)) == true;
    }

    public Task<WinoStorePurchaseResult> PurchaseAsync(WinoAddOnProductType productType) =>
        Task.FromResult(WinoStorePurchaseResult.NotPurchased);

    public async Task<string?> GetCustomerCollectionsIdAsync(string serviceTicket, string publisherUserId)
    {
        if (string.IsNullOrWhiteSpace(serviceTicket) || string.IsNullOrWhiteSpace(publisherUserId))
        {
            return null;
        }

        var id = await context.GetCustomerCollectionsIdAsync(serviceTicket, publisherUserId);
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    public async Task<string?> GetCustomerPurchaseIdAsync(string serviceTicket, string publisherUserId)
    {
        if (string.IsNullOrWhiteSpace(serviceTicket) || string.IsNullOrWhiteSpace(publisherUserId))
        {
            return null;
        }

        var id = await context.GetCustomerPurchaseIdAsync(serviceTicket, publisherUserId);
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }
}
