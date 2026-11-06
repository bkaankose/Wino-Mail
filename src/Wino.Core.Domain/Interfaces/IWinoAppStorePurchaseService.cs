#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Mac App Store purchases and their link to the Wino Account, like the Microsoft Store flow:
/// Unlimited Accounts works without a Wino Account and can be redeemed onto one later; Wino Intelligence
/// needs one. Purchases made while signed in carry the Wino Account ID and are linked before StoreKit
/// finishes them. <see cref="IsAvailable"/> is false on builds without the App Store.
/// </summary>
public interface IWinoAppStorePurchaseService
{
    bool IsAvailable { get; }

    /// <summary>Whether the Apple Account owns Unlimited Accounts, which unlocks it on this Mac.</summary>
    Task<bool> HasUnlimitedAccountsAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether purchases outside the App Store may be offered: the storefront is the United States.</summary>
    Task<bool> IsExternalPurchaseAllowedAsync(CancellationToken cancellationToken = default);

    Task<WinoAppStorePurchaseOutcome> PurchaseAsync(WinoAddOnProductType productType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Links the purchases made for the signed-in Wino Account and finishes delivered transactions.
    /// Runs at launch and when the Wino Account page loads. Returns true when the API answered.
    /// </summary>
    Task<bool> SyncOwnedPurchasesAsync(CancellationToken cancellationToken = default);

    /// <summary>Restore Purchases: refreshes the App Store transactions, then <see cref="SyncOwnedPurchasesAsync"/>.</summary>
    Task<bool> RestoreAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a signed-in Wino Account without Unlimited Accounts can redeem an Unlimited Accounts
    /// purchase that was bought signed out. Purchases the user already tried, or that belong elsewhere, are hidden.
    /// </summary>
    Task<bool> HasRedeemCandidateAsync(CancellationToken cancellationToken = default);

    /// <summary>Links the redeemable Unlimited Accounts purchase to the signed-in Wino Account. Only cancellation throws.</summary>
    Task<WinoStorePurchaseRedeemOutcome> RedeemAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts handling renewals and other transactions that arrive outside a purchase. Call once at launch.</summary>
    void StartTransactionUpdates();
}
