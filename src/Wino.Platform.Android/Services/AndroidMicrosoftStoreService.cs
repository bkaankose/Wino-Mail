using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Android.Services;

/// <summary>
/// The Microsoft Store does not exist on Android, and Play Billing is not part of the first Android release, so there
/// are no purchases or updates. The rating prompt is not shown (Play In-App Review is a later addition); the review
/// page is the app's Play listing.
/// </summary>
public sealed class AndroidMicrosoftStoreService(string packageName, IExternalLauncher launcher) : IMicrosoftStoreService
{
    public bool HasAvailableUpdate => false;
    public Task<bool> HasProductAsync(WinoAddOnProductType productType) => Task.FromResult(false);
    public Task<StorePurchaseResult> PurchaseAsync(WinoAddOnProductType productType) => Task.FromException<StorePurchaseResult>(new PlatformNotSupportedException("Microsoft Store is not available on Android."));
    public Task<string?> GetCustomerCollectionsIdAsync(string serviceTicket, string publisherUserId) => Task.FromResult<string?>(null);
    public Task<bool> RefreshAvailabilityAsync() => Task.FromResult(false);
    public Task<bool> StartUpdateAsync() => Task.FromResult(false);
    public Task PromptRatingDialogAsync() => Task.CompletedTask;

    public async Task LaunchStorePageForReviewAsync()
    {
        var result = await launcher.LaunchUriAsync(new Uri("https://play.google.com/store/apps/details?id=" + Uri.EscapeDataString(packageName))).ConfigureAwait(false);
        if (result.Status != PlatformOperationStatus.Succeeded)
            Serilog.Log.Warning("Could not open the Play Store page: {Error}", result.ErrorMessage);
    }
}
