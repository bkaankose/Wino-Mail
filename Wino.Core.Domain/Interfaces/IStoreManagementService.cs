#nullable enable
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface IStoreManagementService
{
    /// <summary>
    /// Checks whether user has the type of an add-on purchased.
    /// </summary>
    Task<bool> HasProductAsync(WinoAddOnProductType productType);

    /// <summary>
    /// Attempts to purchase the given add-on.
    /// </summary>
    Task<StorePurchaseResult> PurchaseAsync(WinoAddOnProductType productType);

    /// <summary>
    /// Requests a Microsoft Store collections ID key for the current customer.
    /// </summary>
    Task<string?> GetCustomerCollectionsIdAsync(string serviceTicket, string publisherUserId);

    /// <summary>
    /// Requests a Microsoft Store purchase ID key for the current customer.
    /// </summary>
    Task<string?> GetCustomerPurchaseIdAsync(string serviceTicket, string publisherUserId);
}
