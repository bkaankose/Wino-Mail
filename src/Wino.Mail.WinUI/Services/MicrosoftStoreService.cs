using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Windows.Services.Store;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using WinRT.Interop;
using WinoAddOnProductType = Wino.Core.Domain.Enums.WinoAddOnProductType;
using WinoStorePurchaseResult = Wino.Core.Domain.Enums.StorePurchaseResult;

namespace Wino.Mail.WinUI.Services;

/// <summary>
/// One <see cref="StoreContext"/>, one main-window HWND initialization path, for licenses,
/// purchases, the review prompt and package updates.
/// </summary>
public sealed class MicrosoftStoreService : IMicrosoftStoreService
{
    private const string RatedStorageKey = "RatedStorageKey";
    private const string LatestAskedKey = "LatestAskedKey";
    private const string StoreReviewUri = "ms-windows-store://review/?ProductId=9NCRCVJC50WL";

    private static readonly Dictionary<WinoAddOnProductType, string> ProductIds = new()
    {
        { WinoAddOnProductType.UNLIMITED_ACCOUNTS, "UnlimitedAccounts" }
    };

    private static readonly Dictionary<WinoAddOnProductType, string> SkuIds = new()
    {
        { WinoAddOnProductType.UNLIMITED_ACCOUNTS, "9P02MXZ42GSM" }
    };

    private readonly IConfigurationService _configurationService;
    private readonly IWinoLogger _logger;
    private readonly INativeAppService _nativeAppService;

    // Resolved lazily: the dialog service reaches this service again through the Wino account
    // profile, intelligence and billing chain, so a direct constructor dependency would be a cycle.
    private readonly Lazy<IMailDialogService> _dialogService;

    private readonly SemaphoreSlim _operationSemaphore = new(1, 1);
    private StoreContext _storeContext;
    private int _isInstalling;

    public bool HasAvailableUpdate { get; private set; }

    public MicrosoftStoreService(IConfigurationService configurationService,
                                 IWinoLogger logger,
                                 INativeAppService nativeAppService,
                                 Lazy<IMailDialogService> dialogService)
    {
        _configurationService = configurationService;
        _logger = logger;
        _nativeAppService = nativeAppService;
        _dialogService = dialogService;
    }

    #region Licenses and purchases

    public async Task<bool> HasProductAsync(WinoAddOnProductType productType)
    {
        if (!ProductIds.TryGetValue(productType, out var productKey))
            return false;

        var appLicense = await GetContext().GetAppLicenseAsync();

        if (appLicense == null)
            return false;

        // Access the valid licenses for durable add-ons for this app.
        foreach (KeyValuePair<string, StoreLicense> item in appLicense.AddOnLicenses)
        {
            StoreLicense addOnLicense = item.Value;

            if (addOnLicense.InAppOfferToken == productKey)
            {
                return addOnLicense.IsActive;
            }
        }

        return false;
    }

    public async Task<WinoStorePurchaseResult> PurchaseAsync(WinoAddOnProductType productType)
    {
        if (!SkuIds.TryGetValue(productType, out var storeId))
            return WinoStorePurchaseResult.NotPurchased;

        if (await HasProductAsync(productType))
            return WinoStorePurchaseResult.AlreadyPurchased;

        if (WinoApplication.MainWindow == null)
            return WinoStorePurchaseResult.NotPurchased;

        var result = await RunOnMainWindowAsync(async context => await context.RequestPurchaseAsync(storeId));

        return result.Status switch
        {
            StorePurchaseStatus.Succeeded => WinoStorePurchaseResult.Succeeded,
            StorePurchaseStatus.AlreadyPurchased => WinoStorePurchaseResult.AlreadyPurchased,
            _ => WinoStorePurchaseResult.NotPurchased
        };
    }

    #endregion

    #region Rating

    private bool IsAskingThresholdExceeded()
    {
        var latestAskedDate = _configurationService.Get(LatestAskedKey, DateTime.MinValue);

        // Never asked before.
        // Set the threshold and wait for the next trigger.

        if (latestAskedDate == DateTime.MinValue)
        {
            _configurationService.Set(LatestAskedKey, DateTime.UtcNow);
        }
        else if (DateTime.UtcNow >= latestAskedDate.AddMinutes(30))
        {
            return true;
        }

        return false;
    }

    public async Task PromptRatingDialogAsync()
    {
        // Annoying.
        if (Debugger.IsAttached) return;

        // Swallow all exceptions. App should not crash in any errors.

        try
        {
            bool isRated = _configurationService.GetRoaming(RatedStorageKey, false);

            if (isRated) return;

            if (!IsAskingThresholdExceeded()) return;

            var isRateWinoApproved = await _dialogService.Value.ShowWinoCustomMessageDialogAsync(Translator.StoreRatingDialog_Title,
                Translator.StoreRatingDialog_MessageFirstLine,
                Translator.Buttons_RateWino,
                Wino.Core.Domain.Enums.WinoCustomMessageDialogIcon.Question,
                Translator.Buttons_No,
                RatedStorageKey);

            if (isRateWinoApproved)
            {
                // In case of failure of this call, we will navigate users to Store page directly.

                try
                {
                    await ShowPortableRatingDialogAsync();
                }
                catch (Exception)
                {
                    await _nativeAppService.LaunchUriAsync(new Uri(StoreReviewUri));
                }
            }
        }
        catch (Exception) { }
        finally
        {
            _configurationService.Set(LatestAskedKey, DateTime.UtcNow);
        }
    }

    private async Task ShowPortableRatingDialogAsync()
    {
        var dialogService = _dialogService.Value;
        StoreRateAndReviewResult result = await RunOnMainWindowAsync(async context => await context.RequestRateAndReviewAppAsync());

        // Check status
        switch (result.Status)
        {
            case StoreRateAndReviewStatus.Succeeded:
                if (result.WasUpdated)
                    dialogService.InfoBarMessage(Translator.Info_ReviewSuccessTitle, Translator.Info_ReviewUpdatedMessage, Wino.Core.Domain.Enums.InfoBarMessageType.Success);
                else
                    dialogService.InfoBarMessage(Translator.Info_ReviewSuccessTitle, Translator.Info_ReviewNewMessage, Wino.Core.Domain.Enums.InfoBarMessageType.Success);

                _configurationService.Set(RatedStorageKey, true);
                break;
            case StoreRateAndReviewStatus.CanceledByUser:
                break;

            case StoreRateAndReviewStatus.NetworkError:
                dialogService.InfoBarMessage(Translator.Info_ReviewNetworkErrorTitle, Translator.Info_ReviewNetworkErrorMessage, Wino.Core.Domain.Enums.InfoBarMessageType.Warning);
                break;
            default:
                dialogService.InfoBarMessage(Translator.Info_ReviewUnknownErrorTitle, string.Format(Translator.Info_ReviewUnknownErrorMessage, result.ExtendedError.Message), Wino.Core.Domain.Enums.InfoBarMessageType.Warning);
                break;
        }
    }

    public async Task LaunchStorePageForReviewAsync()
    {
        try
        {
            // Routed through the one launcher so the default-browser workaround applies everywhere.
            await _nativeAppService.LaunchUriAsync(new Uri(StoreReviewUri));
        }
        catch (Exception) { }
    }

    #endregion

    #region Updates

    public async Task<bool> RefreshAvailabilityAsync()
    {
        await _operationSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            var updates = await RunOnMainWindowAsync(async context =>
                await context.GetAppAndOptionalStorePackageUpdatesAsync());
            HasAvailableUpdate = updates?.Count > 0;

            return HasAvailableUpdate;
        }
        catch (Exception ex)
        {
            _logger.CaptureException(ex, nameof(RefreshAvailabilityAsync));
            // An unsuccessful check does not invalidate the last successful snapshot.
            return false;
        }
        finally
        {
            _operationSemaphore.Release();
        }
    }

    public async Task<bool> StartUpdateAsync()
    {
        // A legacy toast and the shell prompt can both request installation.
        if (Interlocked.CompareExchange(ref _isInstalling, 1, 0) != 0)
            return false;

        await _operationSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            return await RunOnMainWindowAsync(async context =>
            {
                var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();

                if (updates == null || updates.Count == 0)
                {
                    HasAvailableUpdate = false;
                    return false;
                }

                HasAvailableUpdate = true;
                var result = await context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
                var isCompleted = result?.OverallState == StorePackageUpdateState.Completed;

                if (isCompleted)
                    HasAvailableUpdate = false;

                if (!isCompleted && result != null)
                {
                    _logger.TrackEvent("Store update installation did not complete", new Dictionary<string, string>
                    {
                        { nameof(result.OverallState), result.OverallState.ToString() }
                    });
                }

                return isCompleted;
            });
        }
        catch (Exception ex)
        {
            _logger.CaptureException(ex, nameof(StartUpdateAsync));
            return false;
        }
        finally
        {
            _operationSemaphore.Release();
            Volatile.Write(ref _isInstalling, 0);
        }
    }

    #endregion

    private StoreContext GetContext() => _storeContext ??= StoreContext.GetDefault();

    /// <summary>
    /// Runs a Store operation on the main window's thread with the context bound to its HWND,
    /// which the Store UI (purchase, review, update) requires.
    /// </summary>
    private async Task<T> RunOnMainWindowAsync<T>(Func<StoreContext, Task<T>> operation)
    {
        var mainWindow = WinoApplication.MainWindow
            ?? throw new InvalidOperationException("Main window is not available for the Store operation.");

        var dispatcherQueue = mainWindow.DispatcherQueue
            ?? throw new InvalidOperationException("Main window dispatcher is not available for the Store operation.");

        if (dispatcherQueue.HasThreadAccess)
        {
            return await RunAsync();
        }

        return await dispatcherQueue.EnqueueAsync(RunAsync);

        async Task<T> RunAsync()
        {
            var context = GetContext();
            InitializeWithWindow.Initialize(context, WindowNative.GetWindowHandle(mainWindow));
            return await operation(context);
        }
    }
}
