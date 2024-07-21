using System;
using System.Collections.Generic;
using Microsoft.AppCenter;
using Microsoft.AppCenter.Analytics;
using Microsoft.AppCenter.Crashes;

using Windows.ApplicationModel.Core;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.System.Profile;
using Windows.UI.ViewManagement;
using Wino.Mail.ViewModels;
using Wino.Services;
using Windows.ApplicationModel.AppService;
using Microsoft.Extensions.DependencyInjection;
using Wino.Mail;
using Wino.Shared.WinRT;
using Wino.Shared.WinRT.Services;
using Wino.Domain.Interfaces;






#if NET8_0
using Microsoft.UI.Xaml;
using Microsoft.UI;
#else
using Microsoft.UI.Xaml;
using Windows.UI;
#endif

namespace Wino
{
    public partial class App : Application
    {
        private const string WinoLaunchLogPrefix = "[Wino Launch] ";
        private const string AppCenterKey = "90deb1d0-a77f-47d0-8a6b-7eaf111c6b72";

        private readonly IWinoServerConnectionManager<AppServiceConnection> _appServiceConnectionManager;
        private readonly ILogInitializer _logInitializer;
        private readonly IThemeService _themeService;
        private readonly IDatabaseService _databaseService;
        private readonly ITranslationService _translationService;
        private readonly IAppShellService _appShellService;

        public new static App Current => (App)Application.Current;
        public IServiceProvider Services { get; }

        // Order matters.
        private List<IInitializeAsync> initializeServices => new List<IInitializeAsync>()
        {
            _databaseService,
            _translationService,
            _themeService,
        };

        private IServiceProvider ConfigureServices()
        {
            var services = new ServiceCollection();

            // Registration of the database services and non-synchronization related classes.
            services.RegisterServices();

            // Registration of shared WinRT services.
            services.RegisterCoreUWPServices();

            // Registration of Wino Mail services.
            services.RegisterWinoMailServices();

            // Register Wino Mail viewModels.
            services.RegisterViewModels();

            return services.BuildServiceProvider();
        }

        #region Misc Configuration

        private void ConfigureLogger() => _logInitializer.SetupLogger(ApplicationData.Current.LocalFolder.Path);

        private void ConfigureAppCenter() => AppCenter.Start(AppCenterKey, typeof(Analytics), typeof(Crashes));

        private void ConfigurePrelaunch()
        {
            if (ApiInformation.IsMethodPresent("Windows.ApplicationModel.Core.CoreApplication", "EnablePrelaunch"))
                CoreApplication.EnablePrelaunch(true);
        }

        private void ConfigureXbox()
        {
            // Xbox users should use Reveal focus.
            if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 6))
            {
                FocusVisualKind = AnalyticsInfo.VersionInfo.DeviceFamily == "Xbox" ? FocusVisualKind.Reveal : FocusVisualKind.HighVisibility;
            }
        }

        private void ConfigureTitleBar()
        {
            var coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
            var applicationViewTitleBar = ApplicationView.GetForCurrentView().TitleBar;

            // Extend shell content into core window to meet design requirements.
            coreTitleBar.ExtendViewIntoTitleBar = true;

            // Change system buttons and background colors to meet design requirements.
            applicationViewTitleBar.ButtonBackgroundColor = Colors.Transparent;
            applicationViewTitleBar.BackgroundColor = Colors.Transparent;
            applicationViewTitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            applicationViewTitleBar.ButtonForegroundColor = Colors.White;
        }

        #endregion
    }
}
