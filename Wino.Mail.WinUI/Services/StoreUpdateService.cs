using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Services.Store;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.WinUI.Services;

public class StoreUpdateService : IStoreUpdateService
{
    private const string NotificationShownKeyFormat = "StoreUpdateNotificationShown_{0}";

    private readonly IConfigurationService _configurationService;
    private readonly INotificationBuilder _notificationBuilder;
    private readonly IPreferencesService _preferencesService;
    private readonly INativeAppService _nativeAppService;
    private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);
    private readonly StoreContext _storeContext = StoreContext.GetDefault();

    public bool HasAvailableUpdate { get; private set; }

    public StoreUpdateService(IConfigurationService configurationService,
                              INotificationBuilder notificationBuilder,
                              IPreferencesService preferencesService,
                              INativeAppService nativeAppService)
    {
        _configurationService = configurationService;
        _notificationBuilder = notificationBuilder;
        _preferencesService = preferencesService;
        _nativeAppService = nativeAppService;
    }

    public async Task<bool> RefreshAvailabilityAsync(bool showNotification = false)
    {
        await _refreshSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            var updates = await _storeContext.GetAppAndOptionalStorePackageUpdatesAsync();
            HasAvailableUpdate = updates?.Count > 0;

            if (showNotification &&
                HasAvailableUpdate &&
                _preferencesService.IsStoreUpdateNotificationsEnabled &&
                !HasShownNotificationForCurrentVersion())
            {
                _notificationBuilder.CreateStoreUpdateNotification();
                MarkNotificationShownForCurrentVersion();
            }

            return HasAvailableUpdate;
        }
        catch
        {
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

            await _storeContext.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
            await RefreshAvailabilityAsync(false).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool HasShownNotificationForCurrentVersion()
        => _configurationService.Get(GetNotificationShownKey(), false);

    private void MarkNotificationShownForCurrentVersion()
        => _configurationService.Set(GetNotificationShownKey(), true);

    private string GetNotificationShownKey()
        => string.Format(NotificationShownKeyFormat, _nativeAppService.GetFullAppVersion().Replace(".", "_"));
}
