using Foundation;
using UIKit;
using Wino.Core.Apple.Bindings.StoreKit2;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Platform;

namespace Wino.Platform.iOS.Services;

/// <summary>
/// The Microsoft Store does not exist on iOS; purchases go through <see cref="IAppStoreClient"/> and the Wino Account.
/// Ratings use the system review prompt (StoreKit 2), throttled by <see cref="StoreRatingRequestSchedule"/> like the Mac App Store build.
/// </summary>
public sealed class IOSMicrosoftStoreService(IConfigurationService configurationService, IDispatcher dispatcher) : IMicrosoftStoreService
{
    /// <summary>
    /// The App Store record's Apple ID. The Mac and iOS apps share one record and bundle ID (com.winomail.macos), so the
    /// write-review link is the same on both platforms.
    /// </summary>
    private const string AppStoreAppId = "6820215639";
    private const string FirstTriggerKey = "IOSRatingFirstTriggerUtc";
    private const string LatestRequestKey = "IOSRatingLatestRequestUtc";

    private readonly StoreRatingRequestSchedule _ratingSchedule = new(configurationService, FirstTriggerKey, LatestRequestKey);

    public bool HasAvailableUpdate => false;
    public Task<bool> HasProductAsync(WinoAddOnProductType productType) => Task.FromResult(false);
    public Task<StorePurchaseResult> PurchaseAsync(WinoAddOnProductType productType) => Task.FromException<StorePurchaseResult>(new PlatformNotSupportedException("Microsoft Store is not available on iOS."));
    public Task<string?> GetCustomerCollectionsIdAsync(string serviceTicket, string publisherUserId) => Task.FromResult<string?>(null);
    public Task<bool> RefreshAvailabilityAsync() => Task.FromResult(false);
    public Task<bool> StartUpdateAsync(IProgress<double>? progress = null) => Task.FromResult(false);

    public Task PromptRatingDialogAsync()
    {
        if (System.Diagnostics.Debugger.IsAttached) return Task.CompletedTask;
        if (!_ratingSchedule.TryBeginRequest()) return Task.CompletedTask;

        // The bridge asks the foreground-active window scene; UIKit state is read on the main thread.
        return dispatcher.ExecuteOnUIThread(StoreKit2Client.RequestReview);
    }

    public async Task LaunchStorePageForReviewAsync()
    {
        Task<bool>? open = null;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            var url = NSUrl.FromString($"itms-apps://apps.apple.com/app/id{AppStoreAppId}?action=write-review");
            if (url is not null)
                open = UIApplication.SharedApplication.OpenUrlAsync(url, new UIApplicationOpenUrlOptions());
        });
        if (open is not null && !await open.ConfigureAwait(false))
            Serilog.Log.Warning("iOS declined to open the App Store review page.");
    }
}
