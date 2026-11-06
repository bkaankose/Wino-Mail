#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// StoreKit on Mac App Store builds. Other builds do not register it. Every method reports only
/// transactions that passed StoreKit's on-device verification.
/// </summary>
public interface IAppStoreClient
{
    /// <summary>The App Store storefront country (ISO 3166-1 alpha-3, such as "USA"), or null when unknown.</summary>
    Task<string?> GetStorefrontCountryCodeAsync();

    /// <summary>The purchases the Apple Account currently owns, including active subscriptions.</summary>
    Task<IReadOnlyList<AppStoreTransactionInfo>> GetCurrentEntitlementsAsync();

    /// <summary>Transactions that were delivered but not finished yet.</summary>
    Task<IReadOnlyList<AppStoreTransactionInfo>> GetUnfinishedTransactionsAsync();

    /// <summary>Shows the App Store purchase sheet. The returned transaction is not finished.</summary>
    Task<AppStorePurchase> PurchaseAsync(string productId, Guid? appAccountToken);

    Task FinishAsync(ulong transactionId);

    /// <summary>Restore Purchases: asks the App Store for the latest transactions. May ask the user to sign in.</summary>
    Task SyncAsync();

    /// <summary>Raised for transactions that arrive outside a purchase: renewals, Ask to Buy approvals, other devices.</summary>
    event EventHandler<AppStoreTransactionInfo>? TransactionUpdated;

    void StartTransactionUpdates();
}
