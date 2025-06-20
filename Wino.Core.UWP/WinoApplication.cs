using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Nito.AsyncEx;
using Serilog;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Core;
using Windows.Foundation.Metadata;
using Windows.Globalization;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Core.Preview;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Activation;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Translations;
using Wino.Messaging.Client.Shell;
using Wino.Services;

namespace Wino.Core.UWP;

public abstract class WinoApplication : Application, IRecipient<LanguageChanged>
{
    public new static WinoApplication Current => (WinoApplication)Application.Current;
    public const string WinoLaunchLogPrefix = "[Wino Launch] ";

    public IServiceProvider Services { get; }
    protected IWinoLogger LogInitializer { get; }
    protected IApplicationConfiguration AppConfiguration { get; }
    protected IWinoServerConnectionManager<AppServiceConnection> AppServiceConnectionManager { get; }
    public IThemeService ThemeService { get; }
    public IUnderlyingThemeService UnderlyingThemeService { get; }
    public IThumbnailService ThumbnailService { get; }
    protected IDatabaseService DatabaseService { get; }
    protected ITranslationService TranslationService { get; }

    protected WinoApplication()
    {
        ConfigurePrelaunch();

        Services = ConfigureServices();

        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        UnhandledException += OnAppUnhandledException;

        Resuming += OnResuming;
        Suspending += OnSuspending;

        LogInitializer = Services.GetService<IWinoLogger>();
        AppConfiguration = Services.GetService<IApplicationConfiguration>();

        AppServiceConnectionManager = Services.GetService<IWinoServerConnectionManager<AppServiceConnection>>();
        ThemeService = Services.GetService<IThemeService>();
        DatabaseService = Services.GetService<IDatabaseService>();
        TranslationService = Services.GetService<ITranslationService>();
        UnderlyingThemeService = Services.GetService<IUnderlyingThemeService>();
        ThumbnailService = Services.GetService<IThumbnailService>();

        // Make sure the paths are setup on app start.
        AppConfiguration.ApplicationDataFolderPath = ApplicationData.Current.LocalFolder.Path;
        AppConfiguration.PublisherSharedFolderPath = ApplicationData.Current.GetPublisherCacheFolder(ApplicationConfiguration.SharedFolderName).Path;
        AppConfiguration.ApplicationTempFolderPath = ApplicationData.Current.TemporaryFolder.Path;

        ConfigureLogging();
    }

    private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        => Log.Fatal(e.ExceptionObject as Exception, "AppDomain Unhandled Exception");

    private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        => Log.Error(e.Exception, "Unobserved Task Exception");

    private void OnAppUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled Exception");
        e.Handled = true;
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

    public virtual async void OnResuming(object sender, object e)
    {
        // App Service connection was lost on suspension.
        // We must restore it.
        // Server might be running already, but re-launching it will trigger a new connection attempt.

        try
        {
            await AppServiceConnectionManager.ConnectAsync();
        }
        catch (OperationCanceledException)
        {
            // Ignore
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to connect to server after resuming the app.");
        }
    }
    public virtual void OnSuspending(object sender, SuspendingEventArgs e) { }

    public abstract IServiceProvider ConfigureServices();

    public void ConfigureLogging()
    {
        string logFilePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, Constants.ClientLogFile);
        LogInitializer.SetupLogger(logFilePath);
    }

    public virtual void OnLanguageChanged(AppLanguageModel languageModel)
    {
        var newCulture = new CultureInfo(languageModel.Code);

        ApplicationLanguages.PrimaryLanguageOverride = languageModel.Code;

        CultureInfo.DefaultThreadCurrentCulture = newCulture;
        CultureInfo.DefaultThreadCurrentUICulture = newCulture;
    }

    public void Receive(LanguageChanged message) => OnLanguageChanged(TranslationService.CurrentLanguageModel);
}
