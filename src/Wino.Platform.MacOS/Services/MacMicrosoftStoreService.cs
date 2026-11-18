using AppKit;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Apple.Bindings.StoreKit2;
using Wino.Services.Platform;

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

    private readonly StoreRatingRequestSchedule _ratingSchedule = new(configurationService, FirstTriggerKey, LatestRequestKey);

    public bool HasAvailableUpdate => false;
    public Task<bool> HasProductAsync(WinoAddOnProductType productType) => Task.FromResult(false);
    public Task<StorePurchaseResult> PurchaseAsync(WinoAddOnProductType productType) => Task.FromException<StorePurchaseResult>(new PlatformNotSupportedException("Microsoft Store is not available on macOS."));
    public Task<string?> GetCustomerCollectionsIdAsync(string serviceTicket, string publisherUserId) => Task.FromResult<string?>(null);
    public Task<bool> RefreshAvailabilityAsync() => Task.FromResult(false);
    public Task<bool> StartUpdateAsync(IProgress<double>? progress = null) => Task.FromResult(false);

    public Task PromptRatingDialogAsync()
    {
        if (!isAppStoreBuild || System.Diagnostics.Debugger.IsAttached) return Task.CompletedTask;
        if (!_ratingSchedule.TryBeginRequest()) return Task.CompletedTask;

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
