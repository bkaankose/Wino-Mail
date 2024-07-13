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
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Activation;
using Wino.Core;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;

namespace Wino
{
    public sealed partial class App : Application
    {


        public App()
        {
            InitializeComponent();

            UnhandledException += OnAppUnhandledException;
            EnteredBackground += OnEnteredBackground;
            LeavingBackground += OnLeavingBackground;

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

        private void LogActivation(string log) => Log.Information($"{WinoLaunchLogPrefix}{log}");
        private void OnLeavingBackground(object sender, LeavingBackgroundEventArgs e) => LogActivation($"Wino went foreground.");
        private void OnEnteredBackground(object sender, EnteredBackgroundEventArgs e) => LogActivation($"Wino went background.");

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
            await PreInitializationAsync();

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
    }
}
