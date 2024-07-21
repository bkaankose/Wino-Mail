using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Windows.Storage;
using Wino.Core;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;
using Wino.Core.UWP;
//using Wino.Core.UWP;

namespace Wino.Server
{
    /// <summary>
    /// Single instance Wino Server.
    /// Instancing is done using Mutex.
    /// App will not start if another instance is already running.
    /// App will let running server know that server execution is triggered, which will
    /// led server to start new connection to requesting UWP app.
    /// </summary>
    public partial class App : Application
    {
        private const string NotifyIconResourceKey = "NotifyIcon";
        private const string WinoServerAppName = "Wino.Server";
        private const string WinoServerActiatedName = "Wino.Server.Activated";

        public new static App Current => (App)Application.Current;

        private TaskbarIcon? notifyIcon;
        private static Mutex _mutex = null;
        private EventWaitHandle _eventWaitHandle;

        public IServiceProvider Services { get; private set; }

        private IServiceProvider ConfigureServices()
        {
            var services = new ServiceCollection();

            services.AddTransient<ServerContext>();
            services.AddTransient<ServerViewModel>();

            services.RegisterCoreServices();
            services.RegisterCoreUWPServices();

            // Below services belongs to UWP.Core package and some APIs are not available for WPF.
            // We register them here to avoid compilation errors.

            //services.AddSingleton<IConfigurationService, ConfigurationService>();
            //services.AddSingleton<INativeAppService, NativeAppService>();
            //services.AddSingleton<IPreferencesService, PreferencesService>();

            return services.BuildServiceProvider();
        }

        private async Task<ServerViewModel> InitializeNewServerAsync()
        {
            // TODO: Error handling.

            var databaseService = Services.GetService<IDatabaseService>();
            var applicationFolderConfiguration = Services.GetService<IApplicationConfiguration>();

            applicationFolderConfiguration.ApplicationDataFolderPath = ApplicationData.Current.LocalFolder.Path;
            applicationFolderConfiguration.PublisherSharedFolderPath = ApplicationData.Current.GetPublisherCacheFolder(ApplicationConfiguration.SharedFolderName).Path;

            await databaseService.InitializeAsync();

            var serverViewModel = Services.GetRequiredService<ServerViewModel>();

            await serverViewModel.InitializeAsync();

            return serverViewModel;
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, WinoServerAppName, out bool isCreatedNew);
            _eventWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, WinoServerActiatedName);

            if (isCreatedNew)
            {
                // Spawn a thread which will be waiting for our event
                var thread = new Thread(() =>
                {
                    while (_eventWaitHandle.WaitOne())
                    {
                        if (notifyIcon == null) return;

                        Current.Dispatcher.BeginInvoke(async () =>
                        {
                            if (notifyIcon.DataContext is ServerViewModel trayIconViewModel)
                            {
                                await trayIconViewModel.ReconnectAsync();
                            }
                        });
                    }
                });

                // It is important mark it as background otherwise it will prevent app from exiting.
                thread.IsBackground = true;
                thread.Start();

                Services = ConfigureServices();

                base.OnStartup(e);

                var serverViewModel = await InitializeNewServerAsync();

                // Create taskbar icon for the new server.
                notifyIcon = (TaskbarIcon)FindResource(NotifyIconResourceKey);
                notifyIcon.DataContext = serverViewModel;
                notifyIcon.ForceCreate(enablesEfficiencyMode: true);
            }
            else
            {
                // Notify other instance so it could reconnect to UWP app if needed.
                _eventWaitHandle.Set();

                // Terminate this instance.
                Shutdown();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            notifyIcon?.Dispose();
            base.OnExit(e);
        }
    }
}
