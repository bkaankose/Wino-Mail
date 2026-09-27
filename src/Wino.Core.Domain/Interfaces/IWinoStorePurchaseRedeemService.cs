using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Moves an Unlimited Accounts add-on bought in the Microsoft Store before Wino Accounts existed
/// onto the signed-in Wino Account. The server checks the purchase with the Store; one Store
/// purchase unlocks one Wino Account.
/// </summary>
public interface IWinoStorePurchaseRedeemService
{
    /// <summary>
    /// Redeems the Store purchase when the Wino Account does not have Unlimited Accounts and the
    /// Store license on this device says it was bought. Failures are reported as
    /// <see cref="WinoStorePurchaseRedeemOutcome.Failed"/>; only cancellation throws.
    /// </summary>
    Task<WinoStorePurchaseRedeemOutcome> RedeemUnlimitedAccountsAsync(CancellationToken cancellationToken = default);
}
