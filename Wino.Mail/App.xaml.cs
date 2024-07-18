using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AppCenter.Analytics;
using Microsoft.AppCenter.Crashes;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.Core;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.System.Profile;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Activation;
using Wino.Core;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;
using Wino.Core.WinUI.Services;

namespace Wino
{
    public sealed partial class App : Application
    {


        private BackgroundTaskDeferral backgroundTaskDeferral;

        private readonly IWinoServerConnectionManager<AppServiceConnection> _appServiceConnectionManager;
        private readonly ILogInitializer _logInitializer;
        private readonly IThemeService _themeService;
        private readonly IDatabaseService _databaseService;
        private readonly IApplicationConfiguration _appInitializerService;
        private readonly ITranslationService _translationService;
        private readonly IApplicationConfiguration _applicationFolderConfiguration;

        // Order matters.
        private List<IInitializeAsync> initializeServices => new List<IInitializeAsync>()
        {
            _databaseService,
            _appServiceConnectionManager,
            _translationService,
            _themeService,
        };

        public App()
        {
            InitializeComponent();

            UnhandledException += OnAppUnhandledException;
            EnteredBackground += OnEnteredBackground;
            LeavingBackground += OnLeavingBackground;

            Resuming += OnResuming;
            Suspending += OnSuspending;

            Services = ConfigureServices();

            _logInitializer = Services.GetService<ILogInitializer>();

            ConfigureLogger();
            ConfigureAppCenter();
            ConfigurePrelaunch();
            ConfigureXbox();

            _applicationFolderConfiguration = Services.GetService<IApplicationConfiguration>();

            // Make sure the paths are setup on app start.
            _applicationFolderConfiguration.ApplicationDataFolderPath = ApplicationData.Current.LocalFolder.Path;
            _applicationFolderConfiguration.PublisherSharedFolderPath = ApplicationData.Current.GetPublisherCacheFolder(ApplicationConfiguration.SharedFolderName).Path;

            _appServiceConnectionManager = Services.GetService<IWinoServerConnectionManager<AppServiceConnection>>();
            _themeService = Services.GetService<IThemeService>();
            _databaseService = Services.GetService<IDatabaseService>();
            _appInitializerService = Services.GetService<IApplicationConfiguration>();
            _translationService = Services.GetService<ITranslationService>();
            _appShellService = Services.GetService<IAppShellService>();

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        private async void OnResuming(object sender, object e)
        {
            // App Service connection was lost on suspension.
            // We must restore it.
            // Server might be running already, but re-launching it will trigger a new connection attempt.

            await _appServiceConnectionManager.ConnectAsync();
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            deferral.Complete();
        }

        private void LogActivation(string log) => Log.Information($"{WinoLaunchLogPrefix}{log}");
        private void OnLeavingBackground(object sender, LeavingBackgroundEventArgs e) => LogActivation($"Wino went foreground.");
        private void OnEnteredBackground(object sender, EnteredBackgroundEventArgs e) => LogActivation($"Wino went background.");
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

        #region Dependency Injection

        private void RegisterActivationHandlers(IServiceCollection services)
        {
            services.AddTransient<ProtocolActivationHandler>();
            // services.AddTransient<BackgroundActivationHandler>();
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

        protected override void OnWindowCreated(WindowCreatedEventArgs args)
        {
            base.OnWindowCreated(args);

            _appShellService.AppWindow = args.Window;

            LogActivation("Window is created.");

            ConfigureTitleBar();
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            LogActivation($"OnLaunched -> {args.GetType().Name}, Kind -> {args.Kind}, PreviousExecutionState -> {args.PreviousExecutionState}, IsPrelaunch -> {args.PrelaunchActivated}");

            if (!args.PrelaunchActivated)
            {
                await ActivateWinoAsync(args);
            }
        }

        protected override async void OnFileActivated(FileActivatedEventArgs args)
        {
            base.OnFileActivated(args);

            Log.Information($"File activation for {args.Files.Count} item(s).");

            await ActivateWinoAsync(args);
        }

        protected override async void OnActivated(IActivatedEventArgs args)
        {
            base.OnActivated(args);

            Log.Information($"OnActivated -> {args.GetType().Name}, Kind -> {args.Kind}, Prev Execution State -> {args.PreviousExecutionState}");

            await ActivateWinoAsync(args);
        }

        protected override async void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            base.OnBackgroundActivated(args);

            if (args.TaskInstance.TriggerDetails is AppServiceTriggerDetails appServiceTriggerDetails)
            {
                // Only accept connections from callers in the same package
                if (appServiceTriggerDetails.CallerPackageFamilyName == Package.Current.Id.FamilyName)
                {
                    // Connection established from the fulltrust process

                    backgroundTaskDeferral = args.TaskInstance.GetDeferral();
                    args.TaskInstance.Canceled += OnBackgroundTaskCanceled;

                    _appServiceConnectionManager.Connection = appServiceTriggerDetails.AppServiceConnection;
                }
            }

            LogActivation($"OnBackgroundActivated -> {args.GetType().Name}, TaskInstanceIdName -> {args.TaskInstance?.Task?.Name ?? "NA"}");

            await ActivateWinoAsync(args);
        }

        private void OnAppUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            var parameters = new Dictionary<string, string>()
            {
                { "BaseMessage", e.Exception.GetBaseException().Message },
                { "BaseStackTrace", e.Exception.GetBaseException().StackTrace },
                { "StackTrace", e.Exception.StackTrace },
                { "Message", e.Exception.Message },
            };

            Log.Error(e.Exception, "[Wino Crash]");

            Crashes.TrackError(e.Exception, parameters);
            Analytics.TrackEvent("Wino Crashed", parameters);
        }

        private bool IsInteractiveLaunchArgs(object args) => args is IActivatedEventArgs;

        private async Task ActivateWinoAsync(object args)
        {
            foreach (var service in initializeServices)
            {
                await service.InitializeAsync();
            }

            if (IsInteractiveLaunchArgs(args))
            {
                if (Window.Current.Content == null)
                {
                    var mainFrame = new Frame();

                    Window.Current.Content = mainFrame;

                    await _themeService.InitializeAsync();
                }
            }

            await HandleActivationAsync(args);

            if (IsInteractiveLaunchArgs(args))
            {
                Window.Current.Activate();

                LogActivation("Window activated");
            }
        }

        private async Task HandleActivationAsync(object activationArgs)
        {
            var activationHandler = GetActivationHandlers().FirstOrDefault(h => h.CanHandle(activationArgs));

            if (activationHandler != null)
            {
                await activationHandler.HandleAsync(activationArgs);
            }

            if (IsInteractiveLaunchArgs(activationArgs))
            {
                var defaultHandler = new DefaultActivationHandler();
                if (defaultHandler.CanHandle(activationArgs))
                {
                    await defaultHandler.HandleAsync(activationArgs);
                }
            }
        }

        private IEnumerable<ActivationHandler> GetActivationHandlers()
        {
            yield return Services.GetService<ProtocolActivationHandler>();
            // yield return Services.GetService<BackgroundActivationHandler>(); // Old UWP background task handler.
            yield return Services.GetService<ToastNotificationActivationHandler>();
            yield return Services.GetService<FileActivationHandler>();
        }

        public async void OnBackgroundTaskCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
        {
            Log.Information($"Background task {sender.Task.Name} was canceled. Reason: {reason}");

            await _appServiceConnectionManager.DisconnectAsync();

            backgroundTaskDeferral?.Complete();
            backgroundTaskDeferral = null;

            _appServiceConnectionManager.Connection = null;
        }
    }
}
