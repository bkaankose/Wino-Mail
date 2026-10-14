using Foundation;
using UIKit;
using Wino.Core.Apple.Bindings.StoreKit2;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.iOS.Services;

/// <summary>
/// The Microsoft Store does not exist on iOS; purchases go through <see cref="IAppStoreClient"/> and the Wino Account.
/// Ratings use the system review prompt (StoreKit 2), throttled with the same rules as the Mac App Store build.
/// The throttle is a copy of <c>MacMicrosoftStoreService</c>'s: that class is AppKit-bound and has no reusable core.
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

    /// <summary>Time between the first rating trigger and the first request, so new users are not asked.</summary>
    private static readonly TimeSpan FirstRequestDelay = TimeSpan.FromDays(3);

    /// <summary>The system also limits prompts (three a year); this keeps Wino's own requests rare.</summary>
    private static readonly TimeSpan RequestInterval = TimeSpan.FromDays(120);

    public bool HasAvailableUpdate => false;
    public Task<bool> HasProductAsync(WinoAddOnProductType productType) => Task.FromResult(false);
    public Task<StorePurchaseResult> PurchaseAsync(WinoAddOnProductType productType) => Task.FromException<StorePurchaseResult>(new PlatformNotSupportedException("Microsoft Store is not available on iOS."));
    public Task<string?> GetCustomerCollectionsIdAsync(string serviceTicket, string publisherUserId) => Task.FromResult<string?>(null);
    public Task<bool> RefreshAvailabilityAsync() => Task.FromResult(false);
    public Task<bool> StartUpdateAsync() => Task.FromResult(false);

    public Task PromptRatingDialogAsync()
    {
        if (System.Diagnostics.Debugger.IsAttached) return Task.CompletedTask;

        var now = DateTime.UtcNow;
        var firstTrigger = configurationService.Get(FirstTriggerKey, DateTime.MinValue);
        if (firstTrigger == DateTime.MinValue)
        {
            configurationService.Set(FirstTriggerKey, now);
            return Task.CompletedTask;
        }

        var latestRequest = configurationService.Get(LatestRequestKey, DateTime.MinValue);
        if (now - firstTrigger < FirstRequestDelay || now - latestRequest < RequestInterval) return Task.CompletedTask;

        configurationService.Set(LatestRequestKey, now);
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
