using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AppCenter;
using Microsoft.AppCenter.Analytics;
using Microsoft.AppCenter.Crashes;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Windows.ApplicationModel.Core;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.System.Profile;
using Windows.UI.ViewManagement;
using Wino.Activation;
using Wino.Core;
using Wino.Core.Domain.Interfaces;
using Wino.Core.UWP;
using Wino.Mail.ViewModels;
using Wino.Services;
using Wino.Core.Services;

#if NET8_0
using Microsoft.UI.Xaml;
using Microsoft.UI;
#else
using Windows.UI.Xaml;
using Windows.UI;
#endif

namespace Wino
{
    public partial class App : Application
    {
        private const string WinoLaunchLogPrefix = "[Wino Launch] ";
        private const string AppCenterKey = "90deb1d0-a77f-47d0-8a6b-7eaf111c6b72";

        private readonly ILogInitializer _logInitializer;
        private readonly IThemeService _themeService;
        private readonly IDatabaseService _databaseService;
        private readonly IAppInitializerService _appInitializerService;
        private readonly IWinoSynchronizerFactory _synchronizerFactory;
        private readonly ITranslationService _translationService;

        public new static App Current => (App)Application.Current;
        public IServiceProvider Services { get; }

        // Order matters.
        private List<IInitializeAsync> initializeServices => new List<IInitializeAsync>()
        {
            _translationService,
            _databaseService,
            _themeService,
            _synchronizerFactory
        };

        private IServiceProvider ConfigureServices()
        {
            var services = new ServiceCollection();

            services.RegisterCoreServices();
            services.RegisterCoreUWPServices();

            RegisterUWPServices(services);
            RegisterViewModels(services);
            RegisterActivationHandlers(services);

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

        #region Dependency Injection

        private void RegisterActivationHandlers(IServiceCollection services)
        {
            services.AddTransient<ProtocolActivationHandler>();
            services.AddTransient<BackgroundActivationHandler>();
            services.AddTransient<ToastNotificationActivationHandler>();
            services.AddTransient<FileActivationHandler>();
        }

        private void RegisterUWPServices(IServiceCollection services)
        {
            services.AddSingleton<IApplicationResourceManager<ResourceDictionary>, ApplicationResourceManager>();
            services.AddSingleton<IThemeService, ThemeService>();
            services.AddSingleton<IPreferencesService, PreferencesService>();
            services.AddSingleton<IStatePersistanceService, StatePersistenceService>();
            services.AddSingleton<ILaunchProtocolService, LaunchProtocolService>();
            services.AddSingleton<IWinoNavigationService, WinoNavigationService>();
            services.AddSingleton<IDialogService, DialogService>();
        }

        private void RegisterViewModels(IServiceCollection services)
        {
            services.AddSingleton(typeof(AppShellViewModel));
            services.AddTransient(typeof(SettingsDialogViewModel));
            services.AddTransient(typeof(PersonalizationPageViewModel));
            services.AddTransient(typeof(SettingOptionsPageViewModel));
            services.AddTransient(typeof(MailListPageViewModel));
            services.AddTransient(typeof(MailRenderingPageViewModel));
            services.AddTransient(typeof(AccountManagementViewModel));
            services.AddTransient(typeof(WelcomePageViewModel));
            services.AddTransient(typeof(AboutPageViewModel));
            services.AddTransient(typeof(ComposePageViewModel));
            services.AddTransient(typeof(IdlePageViewModel));
            services.AddTransient(typeof(SettingsPageViewModel));
            services.AddTransient(typeof(NewAccountManagementPageViewModel));
            services.AddTransient(typeof(AccountDetailsPageViewModel));
            services.AddTransient(typeof(SignatureManagementPageViewModel));
            services.AddTransient(typeof(MessageListPageViewModel));
            services.AddTransient(typeof(ReadingPanePageViewModel));
            services.AddTransient(typeof(MergedAccountDetailsPageViewModel));
            services.AddTransient(typeof(LanguageTimePageViewModel));
        }

        #endregion

        /// <summary>
        /// Tasks that must run before the activation and launch.
        /// Regardless of whether it's an interactive launch or not.
        /// </summary>
        private async Task PreInitializationAsync()
        {
            // Handle migrations.
            // TODO: Automate migration process with more proper way.

            if (!ApplicationData.Current.LocalSettings.Values.ContainsKey("Migration_169"))
            {
                try
                {
                    await _appInitializerService.MigrateAsync();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"{WinoLaunchLogPrefix}Migration_169 failed.");
                }
                finally
                {
                    ApplicationData.Current.LocalSettings.Values["Migration_169"] = true;
                }
            }

            foreach (var service in initializeServices)
            {
                await service.InitializeAsync();
            }
        }

        private IEnumerable<ActivationHandler> GetActivationHandlers()
        {
            yield return Services.GetService<ProtocolActivationHandler>();
            yield return Services.GetService<BackgroundActivationHandler>();
            yield return Services.GetService<ToastNotificationActivationHandler>();
            yield return Services.GetService<FileActivationHandler>();
        }
    }
}
