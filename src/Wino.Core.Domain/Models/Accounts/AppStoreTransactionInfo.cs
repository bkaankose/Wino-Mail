#nullable enable
using System;

namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// A verified StoreKit transaction as the platform reports it.
/// </summary>
/// <param name="Id">The transaction ID, used to finish it.</param>
/// <param name="OriginalId">The original transaction ID, which identifies the purchase across renewals and restores.</param>
/// <param name="ProductId">The App Store product ID, such as <see cref="AppStoreProductIds.UnlimitedAccounts"/>.</param>
/// <param name="AppAccountToken">The Wino Account ID the purchase was made for, or null when bought signed out.</param>
/// <param name="SignedTransaction">The JWS that the Wino Account API verifies with Apple.</param>
/// <param name="IsRevoked">Apple refunded or revoked the purchase.</param>
public sealed record AppStoreTransactionInfo(
    ulong Id,
    ulong OriginalId,
    string ProductId,
    Guid? AppAccountToken,
    string SignedTransaction,
    bool IsRevoked);

public enum AppStorePurchaseStatus
{
    Purchased,

    /// <summary>Waiting for approval, such as Ask to Buy. The transaction arrives later as an update.</summary>
    Pending,
    Cancelled
}

/// <param name="Transaction">Set only for <see cref="AppStorePurchaseStatus.Purchased"/>.</param>
public sealed record AppStorePurchase(AppStorePurchaseStatus Status, AppStoreTransactionInfo? Transaction);

/// <summary>The App Store product IDs, which match the Wino product codes.</summary>
public static class AppStoreProductIds
{
    public const string UnlimitedAccounts = "UNLIMITED_ACCOUNTS";
    public const string AiPack = "AI_PACK";
}
