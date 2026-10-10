using AppKit;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.MacOS.Bindings.StoreKit2;

namespace Wino.Platform.MacOS.Services;

/// <summary>
/// Microsoft Store is excluded on Mac. Wino Account billing stays in shared services. Mac App Store
/// builds (<paramref name="isAppStoreBuild"/>) ask for ratings through the system review prompt (StoreKit 2);
/// DMG builds have no store to rate in.
/// </summary>
public sealed class MacMicrosoftStoreService(IConfigurationService configurationService, bool isAppStoreBuild) : IMicrosoftStoreService
{
    private const string AppStoreAppId = "6820215639";
    private const string FirstTriggerKey = "MacRatingFirstTriggerUtc";
    private const string LatestRequestKey = "MacRatingLatestRequestUtc";

    /// <summary>Time between the first rating trigger and the first request, so new users are not asked.</summary>
    private static readonly TimeSpan FirstRequestDelay = TimeSpan.FromDays(3);

    /// <summary>The system also limits prompts (three a year); this keeps Wino's own requests rare.</summary>
    private static readonly TimeSpan RequestInterval = TimeSpan.FromDays(120);

    public bool HasAvailableUpdate => false;
    public Task<bool> HasProductAsync(WinoAddOnProductType productType) => Task.FromResult(false);
    public Task<StorePurchaseResult> PurchaseAsync(WinoAddOnProductType productType) => Task.FromException<StorePurchaseResult>(new PlatformNotSupportedException("Microsoft Store is not available on macOS."));
    public Task<string?> GetCustomerCollectionsIdAsync(string serviceTicket, string publisherUserId) => Task.FromResult<string?>(null);
    public Task<bool> RefreshAvailabilityAsync() => Task.FromResult(false);
    public Task<bool> StartUpdateAsync() => Task.FromResult(false);

    public Task PromptRatingDialogAsync()
    {
        if (!isAppStoreBuild || System.Diagnostics.Debugger.IsAttached) return Task.CompletedTask;

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
        StoreKit2Client.RequestReview();
        return Task.CompletedTask;
    }

    public Task LaunchStorePageForReviewAsync()
    {
        if (!isAppStoreBuild) return Task.CompletedTask;

        NSApplication.SharedApplication.InvokeOnMainThread(() =>
            NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl($"macappstore://apps.apple.com/app/id{AppStoreAppId}?action=write-review")));
        return Task.CompletedTask;
    }
}
