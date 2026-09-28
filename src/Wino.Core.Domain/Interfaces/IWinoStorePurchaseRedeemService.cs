#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Moves an Unlimited Accounts add-on bought in the Microsoft Store onto the signed-in Wino Account
/// when the user asks for it. The server checks the purchase with the Store; one Store purchase
/// unlocks one Wino Account.
/// </summary>
public interface IWinoStorePurchaseRedeemService
{
    /// <summary>
    /// Returns a redeem candidate only when a Wino Account is signed in, the account does not have
    /// Unlimited Accounts, and the Store license on this device says it was bought. Only then does it
    /// request a Store ID key. Returns null when the user already dismissed the redeem for this
    /// Store user, or when the Store ID key cannot be created. Only cancellation throws.
    /// </summary>
    Task<WinoStoreRedeemCandidate?> GetRedeemCandidateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the candidate's Store ID key to the Wino Account API. <see cref="WinoStorePurchaseRedeemOutcome.AlreadyLinked"/>
    /// and <see cref="WinoStorePurchaseRedeemOutcome.NotOwned"/> hide the redeem for that Store user.
    /// Failures are reported as <see cref="WinoStorePurchaseRedeemOutcome.Failed"/>; only cancellation throws.
    /// </summary>
    Task<WinoStorePurchaseRedeemOutcome> RedeemUnlimitedAccountsAsync(WinoStoreRedeemCandidate candidate, CancellationToken cancellationToken = default);
}
