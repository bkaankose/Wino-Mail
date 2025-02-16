using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Store;

namespace Wino.Core.Domain.Interfaces;

public interface IStoreManagementService
{
    /// <summary>
    /// Checks whether user has the type of an add-on purchased.
    /// </summary>
    Task<bool> HasProductAsync(StoreProductType productType);

    /// <summary>
    /// Attempts to purchase the given add-on.
    /// </summary>
    Task<StorePurchaseResult> PurchaseAsync(StoreProductType productType);
}
