using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.MacOS.Bindings.StoreKit2;

namespace Wino.Platform.MacOS.Services;

/// <summary>
/// <see cref="IAppStoreClient"/> over the StoreKit 2 bridge. Registered only in Mac App Store builds.
/// Transactions that fail StoreKit's on-device verification are dropped; the Wino Account API verifies
/// the rest again with Apple.
/// </summary>
public sealed class MacAppStoreClient : IAppStoreClient
{
    public event EventHandler<AppStoreTransactionInfo>? TransactionUpdated;

    public async Task<string?> GetStorefrontCountryCodeAsync()
        => (await StoreKit2Client.GetStorefrontAsync().ConfigureAwait(false))?.CountryCode;

    public async Task<IReadOnlyList<AppStoreTransactionInfo>> GetCurrentEntitlementsAsync()
        => Map(await StoreKit2Client.GetCurrentEntitlementsAsync().ConfigureAwait(false));

    public async Task<IReadOnlyList<AppStoreTransactionInfo>> GetUnfinishedTransactionsAsync()
        => Map(await StoreKit2Client.GetUnfinishedTransactionsAsync().ConfigureAwait(false));

    public async Task<AppStorePurchase> PurchaseAsync(string productId, Guid? appAccountToken)
    {
        var result = await StoreKit2Client.PurchaseAsync(productId, appAccountToken).ConfigureAwait(false);
        return result.Status switch
        {
            StoreKit2PurchaseStatus.UserCancelled => new AppStorePurchase(AppStorePurchaseStatus.Cancelled, null),
            StoreKit2PurchaseStatus.Pending => new AppStorePurchase(AppStorePurchaseStatus.Pending, null),
            _ when result.Transaction is { IsVerified: true } transaction => new AppStorePurchase(AppStorePurchaseStatus.Purchased, Map(transaction)),
            _ => throw new StoreKit2Exception($"The App Store returned an unverified transaction: {result.Transaction?.VerificationError}")
        };
    }

    public Task FinishAsync(ulong transactionId) => StoreKit2Client.FinishAsync(transactionId);

    public Task SyncAsync() => StoreKit2Client.SyncAsync();

    public void StartTransactionUpdates()
    {
        StoreKit2Client.TransactionUpdated += Updated;
        StoreKit2Client.StartTransactionUpdates();
    }

    private void Updated(StoreKit2Transaction transaction)
    {
        if (transaction.IsVerified) TransactionUpdated?.Invoke(this, Map(transaction));
    }

    private static List<AppStoreTransactionInfo> Map(IEnumerable<StoreKit2Transaction> transactions)
        => [.. transactions.Where(t => t.IsVerified).Select(Map)];

    private static AppStoreTransactionInfo Map(StoreKit2Transaction transaction)
        => new(transaction.Id, transaction.OriginalId, transaction.ProductId, transaction.AppAccountToken, transaction.Jws,
            transaction.RevocationDate is not null);
}
