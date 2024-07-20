using System;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;
using Wino.Core.WinUI.Services;
using Wino.Views;
using WinUIEx;
namespace Wino
{
    public partial class App : Application
    {
        private WindowEx m_Window;
        private Frame m_ShellFrame;

        private readonly IApplicationConfiguration _applicationFolderConfiguration;

        public App()
        {
            if (WebAuthenticator.CheckOAuthRedirectionActivation()) return;

            InitializeComponent();

            Services = ConfigureServices();

            _applicationFolderConfiguration = Services.GetService<IApplicationConfiguration>();
            _logInitializer = Services.GetService<ILogInitializer>();

            ConfigureLogger();
            ConfigureAppCenter();
            ConfigurePrelaunch();
            ConfigureXbox();

            // Make sure the paths are setup on app start.
            _applicationFolderConfiguration.ApplicationDataFolderPath = ApplicationData.Current.LocalFolder.Path;
            _applicationFolderConfiguration.PublisherSharedFolderPath = ApplicationData.Current.GetPublisherCacheFolder(ApplicationConfiguration.SharedFolderName).Path;

            _themeService = Services.GetService<IThemeService>();
            _databaseService = Services.GetService<IDatabaseService>();
            _translationService = Services.GetService<ITranslationService>();
            _appShellService = Services.GetService<IAppShellService>();

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            ConfigureWindow();

            _appShellService.AppWindow = m_Window;

            foreach (var service in initializeServices)
            {
                await service.InitializeAsync();
            }

            m_ShellFrame.Navigate(typeof(AppShell));
            m_Window.Activate();
        }

        private void ConfigureWindow()
        {
            m_Window = new WindowEx
            {
                SystemBackdrop = new MicaBackdrop(),
                ExtendsContentIntoTitleBar = true,
                MinWidth = 420
            };

            m_ShellFrame = new Frame();

            m_Window.Content = m_ShellFrame;
        }
    }
}
