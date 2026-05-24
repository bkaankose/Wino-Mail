using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Windows.AppNotifications;
using Serilog;
using Windows.Storage;
using Wino.Core;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Services;
using Wino.Services;

namespace Wino.SyncHost;

internal sealed class SyncHostApplication
{
    private readonly string[] _args;
    private readonly Action _requestMessageLoopExit;
    private readonly CancellationTokenSource _shutdownCts = new();
    private IServiceProvider? _services;
    private SyncHostCommandServer? _commandServer;
    private SyncHostEventPublisher? _eventPublisher;
    private SyncHostEventForwarder? _eventForwarder;
    private BackgroundAutoSyncService? _autoSyncService;
    private SyncHostTrayController? _trayController;
    private SyncHostNotificationActivationRouter? _notificationActivationRouter;

    public SyncHostApplication(
        string[] args,
        Action requestMessageLoopExit)
    {
        _args = args;
        _requestMessageLoopExit = requestMessageLoopExit;
    }

    public CancellationToken ShutdownToken => _shutdownCts.Token;

    public async Task StartAsync()
    {
        _services = ConfigureServices();

        var appConfiguration = _services.GetRequiredService<IApplicationConfiguration>();
        appConfiguration.ApplicationDataFolderPath = ApplicationData.Current.LocalFolder.Path;
        appConfiguration.PublisherSharedFolderPath = ApplicationData.Current.GetPublisherCacheFolder(ApplicationConfiguration.SharedFolderName).Path;
        appConfiguration.ApplicationTempFolderPath = ApplicationData.Current.TemporaryFolder.Path;

        _services
            .GetRequiredService<IWinoLogger>()
            .SetupLogger(Path.Combine(ApplicationData.Current.LocalFolder.Path, Constants.ServerLogFile));

        Log.Information("Starting Wino sync host. Args: {Args}", string.Join(" ", _args));

        AppNotificationManager.Default.Register();

        await _services.GetRequiredService<IDatabaseService>().InitializeAsync().ConfigureAwait(false);
        await _services.GetRequiredService<ITranslationService>().InitializeAsync().ConfigureAwait(false);
        await _services.GetRequiredService<SynchronizationManagerInitializer>().InitializeAsync().ConfigureAwait(false);

        _eventPublisher = _services.GetRequiredService<SyncHostEventPublisher>();
        _eventForwarder = _services.GetRequiredService<SyncHostEventForwarder>();
        _commandServer = _services.GetRequiredService<SyncHostCommandServer>();
        _autoSyncService = _services.GetRequiredService<BackgroundAutoSyncService>();
        _notificationActivationRouter = _services.GetRequiredService<SyncHostNotificationActivationRouter>();

        _eventPublisher.Start(_shutdownCts.Token);
        _eventForwarder.Start();
        _commandServer.Start(_shutdownCts.Token);
        _autoSyncService.Start(_shutdownCts.Token);
        _notificationActivationRouter.Start();

        await _services.GetRequiredService<ICalendarReminderServer>().StartAsync().ConfigureAwait(false);
    }

    public void StartTray()
    {
        if (_services == null)
            return;

        _trayController = _services.GetRequiredService<SyncHostTrayController>();
        _trayController.Create();
    }

    public async Task StopAsync()
    {
        try
        {
            _shutdownCts.Cancel();

            _trayController?.Dispose();

            if (_services != null)
            {
                await _services.GetRequiredService<ICalendarReminderServer>().StopAsync().ConfigureAwait(false);

                if (_services.GetService<ISynchronizationManager>() is { } synchronizationManager)
                {
                    var accounts = await _services.GetRequiredService<IAccountService>().GetAccountsAsync().ConfigureAwait(false);

                    foreach (var account in accounts)
                    {
                        await synchronizationManager.CancelSynchronizationsAsync(account.Id).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed while stopping Wino sync host.");
        }
    }

    public void RequestShutdown()
    {
        if (_shutdownCts.IsCancellationRequested)
            return;

        Log.Information("Sync host shutdown requested.");
        _shutdownCts.Cancel();
        _requestMessageLoopExit();
    }

    private IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.RegisterCoreServices();
        services.RegisterSharedServices();

        services.AddSingleton<IDispatcher, HostDispatcher>();
        services.AddSingleton<INativeAppService, HostNativeAppService>();
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddSingleton<IAuthenticatorConfig, MailAuthenticatorConfiguration>();
        services.AddSingleton<IThumbnailService, ThumbnailService>();
        services.AddSingleton<INotificationBuilder, NotificationBuilder>();
        services.AddSingleton<ICalendarReminderServer, CalendarReminderServer>();
        services.AddSingleton<IKeyPressService, HeadlessKeyPressService>();
        services.AddSingleton<IMailDialogService, HeadlessMailDialogService>();
        services.AddSingleton<IStoreManagementService, HeadlessStoreManagementService>();
        services.AddSingleton<PackagedAppEntryLauncher>();

        services.AddSingleton(this);
        services.AddSingleton<SyncHostCommandServer>();
        services.AddSingleton<SyncHostEventPublisher>();
        services.AddSingleton<SyncHostEventForwarder>();
        services.AddSingleton<SyncHostNotificationActivationRouter>();
        services.AddSingleton<BackgroundAutoSyncService>();
        services.AddSingleton<SyncHostTrayController>();

        return services.BuildServiceProvider();
    }
}
