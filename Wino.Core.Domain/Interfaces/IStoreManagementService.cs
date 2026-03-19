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
}
