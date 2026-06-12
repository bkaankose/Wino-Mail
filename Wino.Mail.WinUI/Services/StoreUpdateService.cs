using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Windows.Services.Store;
using Wino.Core.Domain.Interfaces;
using WinRT.Interop;
using WinUIEx;

namespace Wino.Mail.WinUI.Services;

public class StoreUpdateService : IStoreUpdateService
{
    private readonly IWinoLogger _logger;
    private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);
    private StoreContext? _storeContext;

    public bool HasAvailableUpdate { get; private set; }

    public StoreUpdateService(IWinoLogger logger)
    {
        _logger = logger;
    }

    public async Task<bool> RefreshAvailabilityAsync(bool showNotification = false)
    {
        await _refreshSemaphore.WaitAsync();

        try
        {
            var updates = await GetAppAndOptionalStorePackageUpdatesOnMainWindowAsync();
            HasAvailableUpdate = updates?.Count > 0;

            return HasAvailableUpdate;
        }
        catch (Exception ex)
        {
            _logger.CaptureException(ex, nameof(RefreshAvailabilityAsync));
            HasAvailableUpdate = false;
            return false;
        }
        finally
        {
            _refreshSemaphore.Release();
        }
    }

    public async Task<bool> StartUpdateAsync()
    {
        try
        {
            var updates = await GetAppAndOptionalStorePackageUpdatesOnMainWindowAsync();

            if (updates == null || updates.Count == 0)
            {
                HasAvailableUpdate = false;
                return false;
            }

            var result = await RequestDownloadAndInstallOnMainWindowAsync(updates);
            var isCompleted = result?.OverallState == StorePackageUpdateState.Completed;

            if (!isCompleted && result != null)
            {
                _logger.TrackEvent("Store update installation did not complete", new Dictionary<string, string>
                {
                    { nameof(result.OverallState), result.OverallState.ToString() }
                });
            }

            return isCompleted;
        }
        catch (Exception ex)
        {
            _logger.CaptureException(ex, nameof(StartUpdateAsync));
            return false;
        }
    }

    private async Task<StorePackageUpdateResult> RequestDownloadAndInstallOnMainWindowAsync(IReadOnlyList<StorePackageUpdate> updates)
    {
        var mainWindow = WinoApplication.MainWindow
            ?? throw new InvalidOperationException("Main window is not available for Store update installation.");

        var dispatcherQueue = mainWindow.DispatcherQueue
            ?? throw new InvalidOperationException("Main window dispatcher is not available for Store update installation.");

        if (dispatcherQueue.HasThreadAccess)
        {
            var storeContext = InitializeStoreContextWithWindow(mainWindow);
            return await storeContext.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
        }

        return await dispatcherQueue.EnqueueAsync(async () =>
        {
            var storeContext = InitializeStoreContextWithWindow(mainWindow);
            return await storeContext.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
        });
    }

    private async Task<IReadOnlyList<StorePackageUpdate>?> GetAppAndOptionalStorePackageUpdatesOnMainWindowAsync()
    {
        var mainWindow = WinoApplication.MainWindow
            ?? throw new InvalidOperationException("Main window is not available for Store update availability refresh.");

        var dispatcherQueue = mainWindow.DispatcherQueue
            ?? throw new InvalidOperationException("Main window dispatcher is not available for Store update availability refresh.");

        if (dispatcherQueue.HasThreadAccess)
        {
            var storeContext = InitializeStoreContextWithWindow(mainWindow);
            return await storeContext.GetAppAndOptionalStorePackageUpdatesAsync();
        }

        return await dispatcherQueue.EnqueueAsync(async () =>
        {
            var storeContext = InitializeStoreContextWithWindow(mainWindow);
            return await storeContext.GetAppAndOptionalStorePackageUpdatesAsync();
        });
    }

    private StoreContext InitializeStoreContextWithWindow(WindowEx mainWindow)
    {
        var storeContext = _storeContext ??= StoreContext.GetDefault();
        InitializeWithWindow.Initialize(storeContext, WindowNative.GetWindowHandle(mainWindow));
        return storeContext;
    }

}
