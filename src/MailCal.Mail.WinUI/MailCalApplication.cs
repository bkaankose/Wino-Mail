using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Windows.ApplicationModel.Core;
using Windows.Foundation.Metadata;
using Windows.Storage;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Translations;
using MailCal.Core.Services;
using MailCal.Mail.Controls.ContactPicture;
using MailCal.Mail.WinUI.Interfaces;
using MailCal.Messaging.Client.Shell;
using MailCal.Services;
using WinRT;
using WinUIEx;

namespace MailCal.Mail.WinUI;

public abstract class MailCalApplication : Application, IRecipient<LanguageChanged>
{
    public new static MailCalApplication Current => (MailCalApplication)Application.Current;
    public const string MailCalLaunchLogPrefix = "[Mail-Cal Launch] ";

    public IServiceProvider Services { get; }
    protected IMailCalLogger LogInitializer { get; }
    protected IApplicationConfiguration AppConfiguration { get; }
    public INewThemeService NewThemeService { get; }
    public IUnderlyingThemeService UnderlyingThemeService { get; }
    protected IDatabaseService DatabaseService { get; }
    protected ITranslationService TranslationService { get; }
    private readonly IMailCalWindowManager _windowManager;

    public static WindowEx? MainWindow { get; set; }

    protected MailCalApplication()
    {
        ConfigurePrelaunch();

        Services = ConfigureServices();

        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        UnhandledException += OnAppUnhandledException;

        AppConfiguration = Services.GetRequiredService<IApplicationConfiguration>();

        // Make sure the paths are setup on app start.
        AppConfiguration.ApplicationDataFolderPath = ApplicationData.Current.LocalFolder.Path;
        var releaseIdentity = MailCal.NotificationHost.Contracts.ReleaseIdentity.Current;
        var configuration = (ApplicationConfiguration)AppConfiguration;
        configuration.AllowLegacyDataMigration = releaseIdentity.AllowsLegacyMigration;
        configuration.ApplicationDisplayName = releaseIdentity.DisplayNames["Mail"];
        AppConfiguration.PublisherSharedFolderPath = releaseIdentity.AllowsLegacyMigration
            ? ApplicationData.Current.GetPublisherCacheFolder(ApplicationConfiguration.SharedFolderName).Path
            : string.Empty;
        AppConfiguration.ApplicationTempFolderPath = ApplicationData.Current.TemporaryFolder.Path;
        LegacyNameMigration.Run(AppConfiguration);

        // Constructors in the service graph may read paths. Configure them before
        // resolving logging, storage, authentication, themes or window services.
        LogInitializer = Services.GetRequiredService<IMailCalLogger>();
        NewThemeService = Services.GetRequiredService<INewThemeService>();
        DatabaseService = Services.GetRequiredService<IDatabaseService>();
        TranslationService = Services.GetRequiredService<ITranslationService>();
        _windowManager = Services.GetRequiredService<IMailCalWindowManager>();
        _windowManager.ActiveWindowChanged += WindowManagerActiveWindowChanged;
        UnderlyingThemeService = Services.GetRequiredService<IUnderlyingThemeService>();


        ConfigureLogging();
    }

    private void CurrentDomain_UnhandledException(object? sender, System.UnhandledExceptionEventArgs e)
        => Log.Fatal(e.ExceptionObject as Exception, "AppDomain Unhandled Exception");

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        => Log.Error(e.Exception, "Unobserved Task Exception");

    private void OnAppUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled Exception");
        e.Handled = true;
    }

    public Task InitializeServicesAsync() => Services.GetRequiredService<IApplicationRuntime>().InitializeAsync();

    public bool IsInteractiveLaunchArgs(object args) => args is IActivatedEventArgs;

    public void LogActivation(string log)
        => Log.Information($"{MailCalLaunchLogPrefix}{log} (T+{Stopwatch.GetElapsedTime(Program.StartupTimestamp).TotalMilliseconds:F0} ms)");

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
        if (languageModel == null)
            return;

        var newCulture = new CultureInfo(languageModel.Code);

        ApplicationLanguages.PrimaryLanguageOverride = languageModel.Code;

        CultureInfo.DefaultThreadCurrentCulture = newCulture;
        CultureInfo.DefaultThreadCurrentUICulture = newCulture;

        var flowDirection = newCulture.TextInfo.IsRightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;

        foreach (var window in _windowManager.GetWindows())
        {
            ApplyFlowDirection(window, flowDirection);
        }
    }

    private void WindowManagerActiveWindowChanged(object? sender, WindowEx? window)
    {
        var languageModel = TranslationService.CurrentLanguageModel;
        if (window == null || languageModel == null)
            return;

        var culture = new CultureInfo(languageModel.Code);
        ApplyFlowDirection(window, culture.TextInfo.IsRightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight);
    }

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private static void ApplyFlowDirection(WindowEx window, FlowDirection flowDirection)
    {
        void ApplyToRoot()
        {
            if (window.Content is FrameworkElement rootElement)
            {
                rootElement.FlowDirection = flowDirection;
            }
        }

        if (window.DispatcherQueue.HasThreadAccess)
        {
            ApplyToRoot();
        }
        else
        {
            window.DispatcherQueue.TryEnqueue(ApplyToRoot);
        }
    }

    public void Receive(LanguageChanged message) => OnLanguageChanged(TranslationService.CurrentLanguageModel);
}
