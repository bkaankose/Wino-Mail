#nullable enable
using System;
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
    /// Gets a Store ID key for the Microsoft account signed in to the Store on this device,
    /// so the Wino Account API can check that account's purchases. Null when the Store returns none.
    /// </summary>
    Task<string?> GetCustomerCollectionsIdAsync(string serviceTicket, string publisherUserId);

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

    /// <summary>
    /// The package version of the available update, or null when none is known.
    /// </summary>
    string? AvailableUpdateVersion { get; }

    Task<bool> RefreshAvailabilityAsync();

    /// <summary>
    /// Downloads and installs the available update. Installation closes the app and Windows relaunches it,
    /// so a true result may never be observed. False means nothing was installed.
    /// </summary>
    /// <param name="progress">Download and install progress from 0 to 100.</param>
    Task<bool> StartUpdateAsync(IProgress<double>? progress = null);
}
