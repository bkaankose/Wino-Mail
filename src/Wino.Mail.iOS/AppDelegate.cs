using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using UserNotifications;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Apple.Services;
using Wino.Presentation.UIKit;

namespace Wino.Mail.iOS;

/// <summary>
/// The application delegate: installs the notification delegate and the background task handlers before launching
/// finishes, composes the services and runs the shared startup order (<see cref="ApplicationLifecycleCoordinator"/>).
/// The first page waits for the scene window (<see cref="SceneDelegate"/>).
/// </summary>
[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    private readonly UIKitDispatcher _dispatcher = new();
    private readonly AppleNotificationResponseHandler _notificationResponses = new();
    private Task? _startup;

    /// <summary>The application services, once composition has finished.</summary>
    public static ServiceProvider? Services { get; private set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        Console.WriteLine("Wino Mail iOS: launching");
        // The notification delegate must be in place before launching finishes so a tap that launched the app is kept.
        try { UNUserNotificationCenter.Current.Delegate = _notificationResponses; }
        catch (Exception error) { Console.WriteLine("Wino Mail iOS: could not install the notification delegate: " + error.Message); }
        BackgroundRefreshLifecycleHook.Register();
        WinoIcons.Register();
        Console.WriteLine($"Wino Mail iOS: WinoIcons available={WinoIcons.IsAvailable}");

        Services = Composition.Create(_dispatcher, _notificationResponses);
        _startup = StartAsync(Services);
        // Remote notifications: registration only; the push relay is a separate workstream.
        application.RegisterForRemoteNotifications();
        return true;
    }

    private static async Task StartAsync(IServiceProvider services)
    {
        try
        {
            var target = await services.GetRequiredService<ApplicationLifecycleCoordinator>().StartAsync(services.GetRequiredService<UIKitApplicationLifecycleHost>());
            Console.WriteLine($"Wino Mail iOS: runtime started, showing {target}");
            Serilog.Log.Information("Wino Mail iOS started: {Target}", target);
        }
        catch (Exception error)
        {
            Console.WriteLine("Wino Mail iOS: startup failed: " + error);
            Serilog.Log.Fatal(error, "Wino Mail iOS startup failed.");
        }
    }

    public override UISceneConfiguration GetConfiguration(UIApplication application, UISceneSession connectingSceneSession, UISceneConnectionOptions options)
        => new("Default Configuration", connectingSceneSession.Role) { DelegateType = typeof(SceneDelegate) };

    public override void RegisteredForRemoteNotifications(UIApplication application, NSData deviceToken)
        => Serilog.Log.Information("Registered for remote notifications ({Length} byte token).", deviceToken.Length);

    public override void FailedToRegisterForRemoteNotifications(UIApplication application, NSError error)
        => Serilog.Log.Information("Remote notification registration failed: {Error}", error.LocalizedDescription);

    public override void WillTerminate(UIApplication application)
    {
        if (Services is not { } services) return;
        try
        {
            // Termination gives a few seconds; stop hooks, navigation and the runtime in the shared order.
            var stop = Task.Run(async () =>
            {
                if (_startup is { } startup) await startup;
                await services.GetRequiredService<ApplicationLifecycleCoordinator>().StopAsync(services.GetRequiredService<UIKitApplicationLifecycleHost>());
            });
            stop.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception error) { Serilog.Log.Warning(error, "Stopping Wino Mail iOS failed."); }
    }
}
