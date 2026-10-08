using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.MacOS.Services;

/// <summary>Microsoft Store is excluded on Mac. Wino Account billing stays in shared services.</summary>
public sealed class MacMicrosoftStoreService : IMicrosoftStoreService
{
    public bool HasAvailableUpdate => false;
    public Task<bool> HasProductAsync(WinoAddOnProductType productType) => Task.FromResult(false);
    public Task<StorePurchaseResult> PurchaseAsync(WinoAddOnProductType productType) => Task.FromException<StorePurchaseResult>(new PlatformNotSupportedException("Microsoft Store is not available on macOS."));
    public Task<string?> GetCustomerCollectionsIdAsync(string serviceTicket, string publisherUserId) => Task.FromResult<string?>(null);
    public Task PromptRatingDialogAsync() => Task.FromException(new PlatformNotSupportedException("Microsoft Store is not available on macOS."));
    public Task LaunchStorePageForReviewAsync() => PromptRatingDialogAsync();
    public Task<bool> RefreshAvailabilityAsync() => Task.FromResult(false);
    public Task<bool> StartUpdateAsync() => Task.FromResult(false);
}
