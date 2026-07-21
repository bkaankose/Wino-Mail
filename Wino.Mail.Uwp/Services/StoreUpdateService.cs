using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Services.Store;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Uwp.Services;

public class StoreUpdateService : IStoreUpdateService
{
    private readonly IWinoLogger _logger;
    private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);
    private readonly StoreContext _storeContext = StoreContext.GetDefault();

    public bool HasAvailableUpdate { get; private set; }

    public StoreUpdateService(IWinoLogger logger)
    {
        _logger = logger;
    }

    public async Task<bool> RefreshAvailabilityAsync(bool showNotification = false)
    {
        await _refreshSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            var updates = await _storeContext.GetAppAndOptionalStorePackageUpdatesAsync();
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
            var updates = await _storeContext.GetAppAndOptionalStorePackageUpdatesAsync();

            if (updates == null || updates.Count == 0)
            {
                HasAvailableUpdate = false;
                return false;
            }

            var result = await _storeContext.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
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

}
