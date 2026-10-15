using Android.App;
using Android.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Android;

namespace Wino.Mail.Android;

/// <summary>
/// The process-wide application: builds the service container once (<see cref="AndroidComposition"/>), installs the
/// packaged resources the shared services read, follows the activities and starts the shared runtime when the app
/// comes to the foreground (<see cref="AndroidForegroundLifecycle"/>). The logger and Sentry are set up by the first
/// step of the startup order (<see cref="ApplicationLifecycleCoordinator"/>).
/// </summary>
[Application]
public sealed class MainApplication : global::Android.App.Application
{
    private readonly AndroidActivityTracker _activities = new();

    public MainApplication(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership) { }

    /// <summary>The application services; null only before <see cref="OnCreate"/> finished.</summary>
    public static ServiceProvider? Services { get; private set; }

    /// <summary>The foreground lifecycle; null only before <see cref="OnCreate"/> finished.</summary>
    internal static AndroidForegroundLifecycle? Lifecycle { get; private set; }

    public override void OnCreate()
    {
        base.OnCreate();
        AndroidEnvironment.UnhandledExceptionRaiser += (_, args) =>
        {
            Serilog.Log.Fatal(args.Exception, "Unhandled exception.");
            Services?.GetService<IWinoLogger>()?.CaptureException(args.Exception, "AndroidUnhandledException");
        };

        RegisterActivityLifecycleCallbacks(_activities);
        var services = AndroidComposition.Create(this, _activities);
        Services = services;

        var metadata = services.GetRequiredService<IAppMetadataService>();
        PackagedResourceInstaller.Install(this, services.GetRequiredService<AndroidPaths>(), $"{metadata.AppVersion}+{metadata.SentryDist}");

        Lifecycle = new AndroidForegroundLifecycle(services, _activities, services.GetRequiredService<ApplicationLifecycleCoordinator>(),
            services.GetRequiredService<AndroidApplicationLifecycleHost>());
    }
}
