using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Wino.Core;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;

namespace Wino
{
    public partial class App : Application
    {
        public static MainWindow MainWindow = new MainWindow();

        public App()
        {
            InitializeComponent();

            Services = ConfigureServices();

            _logInitializer = Services.GetService<ILogInitializer>();

            ConfigureLogger();
            ConfigureAppCenter();
            ConfigurePrelaunch();
            ConfigureXbox();

            _themeService = Services.GetService<IThemeService>();
            _databaseService = Services.GetService<IDatabaseService>();
            _appInitializerService = Services.GetService<IAppInitializerService>();
            _synchronizerFactory = Services.GetService<IWinoSynchronizerFactory>();
            _translationService = Services.GetService<ITranslationService>();

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            foreach (var service in initializeServices)
            {
                await service.InitializeAsync();
            }

            MainWindow.Activate();
            MainWindow.StartWino();
        }
    }
}
