using System.Runtime.InteropServices;
using System.Text.Json;

namespace Wino.Core.MacOS.Bindings.StoreKit2;

/// <summary>
/// StoreKit 2 for the Mac App Store build. Calls work in any build, but the App Store only answers
/// for a signed app distributed through it, a TestFlight build, or a StoreKit configuration in testing.
/// Failures surface as <see cref="StoreKit2Exception"/>.
/// </summary>
public static class StoreKit2Client
{
    private static readonly Lock UpdatesLock = new();
    private static bool _listening;

    /// <summary>
    /// A transaction arrived outside <see cref="PurchaseAsync"/>: a renewal, refund, Ask to Buy approval
    /// or purchase on another device. Raised on a background thread after <see cref="StartTransactionUpdates"/>.
    /// </summary>
    public static event Action<StoreKit2Transaction>? TransactionUpdated;

    /// <summary>The current storefront, or null when no Apple Account is signed in to the App Store.</summary>
    public static Task<StoreKit2Storefront?> GetStorefrontAsync()
        => StoreKit2Native.CallAsync(context => StoreKit2Native.Storefront(StoreKit2Native.ReplyCallback, context),
            StoreKit2JsonContext.Default.StoreKit2EnvelopeStoreKit2Storefront);

    /// <summary>Products configured in App Store Connect. Unknown identifiers are left out.</summary>
    public static async Task<IReadOnlyList<StoreKit2Product>> GetProductsAsync(IEnumerable<string> productIds)
    {
        var idsJson = JsonSerializer.Serialize(productIds.ToArray(), StoreKit2JsonContext.Default.StringArray);
        return await StoreKit2Native.CallAsync(context => StoreKit2Native.Products(idsJson, StoreKit2Native.ReplyCallback, context),
            StoreKit2JsonContext.Default.StoreKit2EnvelopeStoreKit2ProductArray).ConfigureAwait(false) ?? [];
    }

    /// <summary>
    /// Shows the App Store purchase sheet. <paramref name="appAccountToken"/> ties the purchase to a
    /// Wino Account and comes back in App Store Server Notifications. Call <see cref="FinishAsync"/>
    /// once the purchase is granted.
    /// </summary>
    public static async Task<StoreKit2PurchaseResult> PurchaseAsync(string productId, Guid? appAccountToken = null)
    {
        var token = appAccountToken?.ToString();
        return await StoreKit2Native.CallAsync(context => StoreKit2Native.Purchase(productId, token, StoreKit2Native.ReplyCallback, context),
            StoreKit2JsonContext.Default.StoreKit2EnvelopeStoreKit2PurchaseResult).ConfigureAwait(false)
            ?? throw new StoreKit2Exception("StoreKit returned no purchase result.");
    }

    /// <summary>The latest transaction of every product the user is currently entitled to.</summary>
    public static async Task<IReadOnlyList<StoreKit2Transaction>> GetCurrentEntitlementsAsync()
        => await StoreKit2Native.CallAsync(context => StoreKit2Native.CurrentEntitlements(StoreKit2Native.ReplyCallback, context),
            StoreKit2JsonContext.Default.StoreKit2EnvelopeStoreKit2TransactionArray).ConfigureAwait(false) ?? [];

    /// <summary>Transactions not finished yet, for example after the app quit before granting one.</summary>
    public static async Task<IReadOnlyList<StoreKit2Transaction>> GetUnfinishedTransactionsAsync()
        => await StoreKit2Native.CallAsync(context => StoreKit2Native.Unfinished(StoreKit2Native.ReplyCallback, context),
            StoreKit2JsonContext.Default.StoreKit2EnvelopeStoreKit2TransactionArray).ConfigureAwait(false) ?? [];

    /// <summary>Finishes a granted transaction. Returns false when it is not among the unfinished ones.</summary>
    public static Task<bool> FinishAsync(ulong transactionId)
        => StoreKit2Native.CallAsync(context => StoreKit2Native.Finish(transactionId, StoreKit2Native.ReplyCallback, context),
            StoreKit2JsonContext.Default.StoreKit2EnvelopeBoolean);

    /// <summary>Restore Purchases. May ask the user to sign in to the App Store.</summary>
    public static Task SyncAsync()
        => StoreKit2Native.CallAsync(context => StoreKit2Native.Sync(StoreKit2Native.ReplyCallback, context),
            StoreKit2JsonContext.Default.StoreKit2EnvelopeBoolean);

    /// <summary>
    /// Asks the system to show the rating prompt over the key window. The system limits how often it
    /// appears and does not report whether it did.
    /// </summary>
    public static void RequestReview() => StoreKit2Native.RequestReview();

    /// <summary>Starts raising <see cref="TransactionUpdated"/>. Call once at launch so no transaction is missed.</summary>
    public static void StartTransactionUpdates()
    {
        lock (UpdatesLock)
        {
            if (_listening) return;
            StoreKit2Native.StartUpdates(UpdateCallback, 0);
            _listening = true;
        }
    }

    public static void StopTransactionUpdates()
    {
        lock (UpdatesLock)
        {
            if (!_listening) return;
            StoreKit2Native.StopUpdates();
            _listening = false;
        }
    }

    private static unsafe nint UpdateCallback => (nint)(delegate* unmanaged<nint, nint, void>)&OnTransactionUpdate;

    [UnmanagedCallersOnly]
    private static void OnTransactionUpdate(nint context, nint json)
    {
        try
        {
            var transaction = StoreKit2Native.Unwrap(Marshal.PtrToStringUTF8(json) ?? string.Empty,
                StoreKit2JsonContext.Default.StoreKit2EnvelopeStoreKit2Transaction);
            if (transaction is not null) TransactionUpdated?.Invoke(transaction);
        }
        catch
        {
            // An exception must not cross back into Swift; a bad update is dropped. Current
            // entitlements still report the transaction on the next check.
        }
    }
}
