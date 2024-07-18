using System;
using System.Threading;
using System.Windows;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core;

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
            services.AddTransient<TrayIconViewModel>();

            services.RegisterCoreServices();

            return services.BuildServiceProvider();
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            bool isCreatedNew;

            _mutex = new Mutex(true, WinoServerAppName, out isCreatedNew);
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
                            if (notifyIcon.DataContext is TrayIconViewModel trayIconViewModel)
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

                // Create taskbar icon for the new server.
                notifyIcon = (TaskbarIcon)FindResource("NotifyIcon");

                var viewModel = Services.GetRequiredService<TrayIconViewModel>();
                await viewModel.Context.InitializeAsync();

                notifyIcon.DataContext = viewModel;
                notifyIcon.ForceCreate(enablesEfficiencyMode: true);
            }
            else
            {
                // Notify other instance so it could bring itself to foreground.
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
