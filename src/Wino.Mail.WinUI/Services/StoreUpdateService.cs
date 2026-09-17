using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Windows.Services.Store;
using Wino.Core.Domain.Interfaces;
using WinRT.Interop;

namespace Wino.Mail.WinUI.Services;

public class StoreUpdateService : IStoreUpdateService
{
    private readonly IWinoLogger _logger;
    private readonly SemaphoreSlim _operationSemaphore = new(1, 1);
    private StoreContext _storeContext;
    private int _isInstalling;

    public bool HasAvailableUpdate { get; private set; }

    public StoreUpdateService(IWinoLogger logger)
    {
        _logger = logger;
    }

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

    private async Task<T> RunOnMainWindowAsync<T>(Func<StoreContext, Task<T>> operation)
    {
        var mainWindow = WinoApplication.MainWindow
            ?? throw new InvalidOperationException("Main window is not available for Store update installation.");

        var dispatcherQueue = mainWindow.DispatcherQueue
            ?? throw new InvalidOperationException("Main window dispatcher is not available for Store update installation.");

        if (dispatcherQueue.HasThreadAccess)
        {
            return await RunAsync();
        }

        return await dispatcherQueue.EnqueueAsync(RunAsync);

        async Task<T> RunAsync()
        {
            _storeContext ??= StoreContext.GetDefault();
            InitializeWithWindow.Initialize(_storeContext, WindowNative.GetWindowHandle(mainWindow));
            return await operation(_storeContext);
        }
    }

}
