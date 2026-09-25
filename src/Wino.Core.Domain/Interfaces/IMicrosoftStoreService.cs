#nullable enable
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Everything the app asks the Microsoft Store for: add-on licenses and purchases,
/// the rate-and-review prompt, and package updates. MSIX only.
/// </summary>
public interface IMicrosoftStoreService
{
    /// <summary>
    /// Checks whether user has the type of an add-on purchased.
    /// </summary>
    Task<bool> HasProductAsync(WinoAddOnProductType productType);

    /// <summary>
    /// Requests a purchase through the Microsoft Store account signed in on this device.
    /// </summary>
    Task<StorePurchaseResult> PurchaseAsync(WinoAddOnProductType productType);

    /// <summary>
    /// Asks the user to rate Wino once the asking threshold has passed, then opens the Store review flow.
    /// </summary>
    Task PromptRatingDialogAsync();

    /// <summary>
    /// Opens the Store review page for Wino.
    /// </summary>
    Task LaunchStorePageForReviewAsync();

    /// <summary>
    /// Whether the last availability check found a package update. Cached until the next refresh.
    /// </summary>
    bool HasAvailableUpdate { get; }

    Task<bool> RefreshAvailabilityAsync();

    Task<bool> StartUpdateAsync();
}
