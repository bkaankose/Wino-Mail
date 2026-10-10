#if WINO_APPSTORE
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.MacOS;

/// <summary>
/// Mac App Store builds: handle renewals and other transactions that arrive outside a purchase, and
/// link the purchases made for the signed-in Wino Account (retrying links that failed earlier).
/// </summary>
public sealed partial class AppDelegate
{
    partial void AppStoreServicesReady()
    {
        if (_services?.GetService<IWinoAppStorePurchaseService>() is not { IsAvailable: true } purchases) return;

        purchases.StartTransactionUpdates();
        Observe(purchases.SyncOwnedPurchasesAsync());
    }
}
#endif
