using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AppCenter;
using Microsoft.AppCenter.Analytics;
using Microsoft.AppCenter.Crashes;
using Microsoft.Extensions.DependencyInjection;
using Nito.AsyncEx;
using Serilog;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Core;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Core.Preview;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Activation;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;

namespace Wino.Core.UWP
{
    public abstract class WinoApplication : Application
    {
        public new static WinoApplication Current => (WinoApplication)Application.Current;
        public const string WinoLaunchLogPrefix = "[Wino Launch] ";

        public IServiceProvider Services { get; }
        protected ILogInitializer LogInitializer { get; }
        protected IApplicationConfiguration AppConfiguration { get; }
        protected IWinoServerConnectionManager<AppServiceConnection> AppServiceConnectionManager { get; }
        protected IThemeService ThemeService { get; }
        protected IDatabaseService DatabaseService { get; }
        protected ITranslationService TranslationService { get; }
        protected IMailDialogService DialogService { get; }

        // Order matters.
        private List<IInitializeAsync> initializeServices => new List<IInitializeAsync>()
        {
            DatabaseService,
            TranslationService,
            ThemeService,
        };

        public abstract string AppCenterKey { get; }

        protected WinoApplication()
        {
            ConfigureAppCenter();
            ConfigurePrelaunch();

            Services = ConfigureServices();

            UnhandledException += OnAppUnhandledException;
            Resuming += OnResuming;
            Suspending += OnSuspending;

            LogInitializer = Services.GetService<ILogInitializer>();
            AppConfiguration = Services.GetService<IApplicationConfiguration>();

            AppServiceConnectionManager = Services.GetService<IWinoServerConnectionManager<AppServiceConnection>>();
            ThemeService = Services.GetService<IThemeService>();
            DatabaseService = Services.GetService<IDatabaseService>();
            TranslationService = Services.GetService<ITranslationService>();
            DialogService = Services.GetService<IMailDialogService>();

            // Make sure the paths are setup on app start.
            AppConfiguration.ApplicationDataFolderPath = ApplicationData.Current.LocalFolder.Path;
            AppConfiguration.PublisherSharedFolderPath = ApplicationData.Current.GetPublisherCacheFolder(ApplicationConfiguration.SharedFolderName).Path;
            AppConfiguration.ApplicationTempFolderPath = ApplicationData.Current.TemporaryFolder.Path;

            ConfigureLogging();
        }

        protected abstract void OnApplicationCloseRequested(object sender, SystemNavigationCloseRequestedPreviewEventArgs e);
        protected abstract IEnumerable<ActivationHandler> GetActivationHandlers();
        protected abstract ActivationHandler<IActivatedEventArgs> GetDefaultActivationHandler();
        protected override void OnWindowCreated(WindowCreatedEventArgs args)
        {
            base.OnWindowCreated(args);

            ConfigureTitleBar();

            LogActivation($"OnWindowCreated -> IsWindowNull: {args.Window == null}");

            TryRegisterAppCloseChange();
        }

        public IEnumerable<IInitializeAsync> GetActivationServices()
        {
            yield return DatabaseService;
            yield return TranslationService;
            yield return ThemeService;
        }

        public Task InitializeServicesAsync() => GetActivationServices().Select(a => a.InitializeAsync()).WhenAll();

        public bool IsInteractiveLaunchArgs(object args) => args is IActivatedEventArgs;

        public void LogActivation(string log) => Log.Information($"{WinoLaunchLogPrefix}{log}");

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

        public async Task ActivateWinoAsync(object args)
        {
            await InitializeServicesAsync();

            if (IsInteractiveLaunchArgs(args))
            {
                if (Window.Current.Content == null)
                {
                    var mainFrame = new Frame();

                    Window.Current.Content = mainFrame;

                    await ThemeService.InitializeAsync();
                }
            }

            await HandleActivationAsync(args);

            if (IsInteractiveLaunchArgs(args))
            {
                Window.Current.Activate();

                LogActivation("Window activated");
            }
        }

        public async Task HandleActivationAsync(object activationArgs)
        {
            if (GetActivationHandlers() != null)
            {
                var activationHandler = GetActivationHandlers().FirstOrDefault(h => h.CanHandle(activationArgs)) ?? null;

                if (activationHandler != null)
                {
                    await activationHandler.HandleAsync(activationArgs);
                }
            }

            if (IsInteractiveLaunchArgs(activationArgs))
            {
                var defaultHandler = GetDefaultActivationHandler();

                if (defaultHandler.CanHandle(activationArgs))
                {
                    await defaultHandler.HandleAsync(activationArgs);
                }
            }
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

            LogActivation($"OnFileActivated -> ItemCount: {args.Files.Count}, Kind: {args.Kind}, PreviousExecutionState: {args.PreviousExecutionState}");

            await ActivateWinoAsync(args);
        }

        protected override async void OnActivated(IActivatedEventArgs args)
        {
            base.OnActivated(args);

            Log.Information($"OnActivated -> {args.GetType().Name}, Kind -> {args.Kind}, Prev Execution State -> {args.PreviousExecutionState}");

            await ActivateWinoAsync(args);
        }

        private void TryRegisterAppCloseChange()
        {
            try
            {
                var systemNavigationManagerPreview = SystemNavigationManagerPreview.GetForCurrentView();

                systemNavigationManagerPreview.CloseRequested -= OnApplicationCloseRequested;
                systemNavigationManagerPreview.CloseRequested += OnApplicationCloseRequested;
            }
            catch { }
        }



        private void ConfigurePrelaunch()
        {
            if (ApiInformation.IsMethodPresent("Windows.ApplicationModel.Core.CoreApplication", "EnablePrelaunch"))
                CoreApplication.EnablePrelaunch(true);
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

        public virtual void OnResuming(object sender, object e) { }
        public virtual void OnSuspending(object sender, SuspendingEventArgs e) { }

        public abstract IServiceProvider ConfigureServices();
        public void ConfigureAppCenter()
            => AppCenter.Start(AppCenterKey, typeof(Analytics), typeof(Crashes));

        public void ConfigureLogging()
        {
            string logFilePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, Constants.ClientLogFile);
            LogInitializer.SetupLogger(logFilePath);
        }
    }
}
