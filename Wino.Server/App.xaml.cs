using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Windows.Storage;
using Wino.Core;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;
using Wino.Core.UWP.Services;
using Wino.Server.Core;
using Wino.Server.MessageHandlers;
using Wino.Services;

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
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private const string FRAME_WINDOW = "ApplicationFrameWindow";
        public const string WinoMailLaunchProtocol = "wino.mail.launch";

        private const string NotifyIconResourceKey = "NotifyIcon";
        private const string WinoServerAppName = "Wino.Server";
        private const string WinoServerActivatedName = "Wino.Server.Activated";

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

            // Below services belongs to UWP.Core package and some APIs are not available for WPF.
            // We register them here to avoid compilation errors.

            services.AddSingleton<IConfigurationService, ConfigurationService>();
            services.AddSingleton<INativeAppService, NativeAppService>();
            services.AddSingleton<IPreferencesService, PreferencesService>();
            services.AddTransient<INotificationBuilder, NotificationBuilder>();
            services.AddTransient<IUnderlyingThemeService, UnderlyingThemeService>();

            // Register server message handler factory.
            var serverMessageHandlerFactory = new ServerMessageHandlerFactory();
            serverMessageHandlerFactory.Setup(services);

            services.AddSingleton<IServerMessageHandlerFactory>(serverMessageHandlerFactory);

            return services.BuildServiceProvider();
        }

        private async Task<ServerViewModel> InitializeNewServerAsync()
        {
            // Make sure app config is setup before anything else.
            var applicationFolderConfiguration = Services.GetService<IApplicationConfiguration>();

            applicationFolderConfiguration.ApplicationDataFolderPath = ApplicationData.Current.LocalFolder.Path;
            applicationFolderConfiguration.PublisherSharedFolderPath = ApplicationData.Current.GetPublisherCacheFolder(ApplicationConfiguration.SharedFolderName).Path;
            applicationFolderConfiguration.ApplicationTempFolderPath = ApplicationData.Current.TemporaryFolder.Path;

            // Setup logger
            var logInitializer = Services.GetService<ILogInitializer>();
            var logFilePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, Constants.ServerLogFile);

            logInitializer.SetupLogger(logFilePath);

            // Make sure the database is ready.
            var databaseService = Services.GetService<IDatabaseService>();
            await databaseService.InitializeAsync();

            // Setup core window handler for native app service.
            // WPF app uses UWP app's window handle to display authentication dialog.
            var nativeAppService = Services.GetService<INativeAppService>();
            nativeAppService.GetCoreWindowHwnd = FindUWPClientWindowHandle;

            // Initialize translations.
            var translationService = Services.GetService<ITranslationService>();
            await translationService.InitializeAsync();

            // Make sure all accounts have synchronizers.
            var synchronizerFactory = Services.GetService<ISynchronizerFactory>();
            await synchronizerFactory.InitializeAsync();

            // Load up the server view model.
            var serverViewModel = Services.GetRequiredService<ServerViewModel>();
            await serverViewModel.InitializeAsync();

            return serverViewModel;
        }

        /// <summary>
        /// OutlookAuthenticator for WAM requires window handle to display the dialog.
        /// Since server app is windowless, we need to find the UWP app window handle.
        /// </summary>
        /// <param name="proc"></param>
        /// <returns>Pointer to running UWP app's hwnd.</returns>
        private IntPtr FindUWPClientWindowHandle()
        {
            // TODO: Resilient.
            var proc = Process.GetProcessesByName("Wino")[0];

            for (IntPtr appWindow = FindWindowEx(IntPtr.Zero, IntPtr.Zero, FRAME_WINDOW, null); appWindow != IntPtr.Zero;
                appWindow = FindWindowEx(IntPtr.Zero, appWindow, FRAME_WINDOW, null))
            {
                IntPtr coreWindow = FindWindowEx(appWindow, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                if (coreWindow != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(coreWindow, out var corePid);
                    if (corePid == proc.Id)
                    {
                        return appWindow;
                    }
                }
            }

            return IntPtr.Zero;
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, WinoServerAppName, out bool isCreatedNew);
            _eventWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, WinoServerActivatedName);

            if (isCreatedNew)
            {
                // Ensure proper encodings are available for MimeKit
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

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

                // Hide the icon if user has set it to invisible.
                var preferencesService = Services.GetService<IPreferencesService>();
                ChangeNotifyIconVisiblity(preferencesService.ServerTerminationBehavior != ServerBackgroundMode.Invisible);
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

        public void ChangeNotifyIconVisiblity(bool isVisible)
        {
            if (notifyIcon == null) return;

            Current.Dispatcher.BeginInvoke(() =>
            {
                notifyIcon.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            });
        }
    }
}
