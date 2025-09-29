using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Globalization;
using Nito.AsyncEx;
using Serilog;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Core;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Translations;
using Wino.Messaging.Client.Shell;
using Wino.Services;
using WinUIEx;

namespace Wino.Core.WinUI;

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

    public static WindowEx MainWindow { get; set; }

    protected WinoApplication()
    {
        ConfigurePrelaunch();

        Services = ConfigureServices();

        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        UnhandledException += OnAppUnhandledException;

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

    private void OnAppUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled Exception");
        e.Handled = true;
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

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        LogActivation("OnLaunched.");
    }

    private void ConfigurePrelaunch()
    {
        if (ApiInformation.IsMethodPresent("Windows.ApplicationModel.Core.CoreApplication", "EnablePrelaunch"))
            CoreApplication.EnablePrelaunch(true);
    }

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
