using System;
using System.Threading.Tasks;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Storage;
using Wino.Core;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;
using Wino.Core.UWP.Services;
using Wino.Services;

namespace Wino.Server.NET8
{
    public partial class ServerApp : Application
    {
        public new static ServerApp Current => (ServerApp)Application.Current;

        private const string WinoServerAppName = "Wino.Server";

        public TaskbarIcon TrayIcon { get; private set; }
        public bool HandleClosedEvents { get; set; } = true;
        public IServiceProvider Services { get; private set; }
        public ServerApp()
        {
            InitializeComponent();
        }

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

            return services.BuildServiceProvider();
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            Services = ConfigureServices();

            await InitializeNewServerAsync();
            InitializeTrayIcon();
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

        private void InitializeTrayIcon()
        {
            var viewModel = Services.GetService<ServerViewModel>();

            var launchCommand = (XamlUICommand)Resources["LaunchCommand"];
            launchCommand.Command = viewModel.LaunchWinoCommand;

            var exitApplicationCommand = (XamlUICommand)Resources["TerminateCommand"];
            exitApplicationCommand.Command = viewModel.ExitApplicationCommand;

            TrayIcon = (TaskbarIcon)Resources["TrayIcon"];
            TrayIcon.ForceCreate();
        }
    }
}
