using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using UserNotifications;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Apple.PushRelay;
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
        // The APNs token goes to ApplePushTokenProvider; the shared relay service registers this device with WinoRelay
        // for the signed-in Wino Account and keeps the token current.
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
    {
        Serilog.Log.Information("Registered for remote notifications ({Length} byte token).", deviceToken.Length);
        Services?.GetService<ApplePushTokenProvider>()?.SetToken(deviceToken);
    }

    public override void FailedToRegisterForRemoteNotifications(UIApplication application, NSError error)
    {
        // Expected in the Simulator and in builds without the aps-environment entitlement; the device registers without a token.
        Serilog.Log.Information("Remote notification registration failed: {Error}", error.LocalizedDescription);
        Services?.GetService<ApplePushTokenProvider>()?.SetFailure(error.LocalizedDescription);
    }

    /// <summary>
    /// A data-only relay push (content-available). iOS allows about 30 seconds; the handler gets 25, and the completion
    /// handler is called exactly once with the outcome, also when startup or the handler does not finish in time.
    /// </summary>
    public override void DidReceiveRemoteNotification(UIApplication application, NSDictionary userInfo, Action<UIBackgroundFetchResult> completionHandler)
    {
        var payload = ApplePushPayload.ToPayload(userInfo);
        var completed = 0;
        void Complete(UIBackgroundFetchResult result)
        {
            if (Interlocked.Exchange(ref completed, 1) == 0) completionHandler(result);
        }
        _ = CompleteRemoteNotificationAsync(payload, Complete);
    }

    private static readonly TimeSpan RemoteNotificationBudget = TimeSpan.FromSeconds(25);

    private async Task CompleteRemoteNotificationAsync(Dictionary<string, string> payload, Action<UIBackgroundFetchResult> complete)
    {
        var result = UIBackgroundFetchResult.Failed;
        try
        {
            using var budget = new CancellationTokenSource(RemoteNotificationBudget);
            var work = HandleRemoteNotificationAsync(payload, budget.Token);
            if (await Task.WhenAny(work, Task.Delay(RemoteNotificationBudget)) == work)
            {
                result = await work;
            }
            else
            {
                budget.Cancel();
                Serilog.Log.Warning("Relay push handling did not finish within {Seconds} s.", RemoteNotificationBudget.TotalSeconds);
            }
        }
        catch (Exception error) { Serilog.Log.Warning(error, "Relay push handling failed."); }
        finally { complete(result); }
    }

    private async Task<UIBackgroundFetchResult> HandleRemoteNotificationAsync(Dictionary<string, string> payload, CancellationToken cancellationToken)
    {
        try
        {
            if (_startup is { } startup) await startup.WaitAsync(cancellationToken);
            if (Services?.GetService<IPushRelayNotificationHandler>() is not { } handler) return UIBackgroundFetchResult.NoData;
            var outcome = await handler.HandleAsync(payload, cancellationToken);
            return outcome switch
            {
                PushRelayNotificationOutcome.NewData => UIBackgroundFetchResult.NewData,
                PushRelayNotificationOutcome.Failed => UIBackgroundFetchResult.Failed,
                _ => UIBackgroundFetchResult.NoData,
            };
        }
        catch (OperationCanceledException) { return UIBackgroundFetchResult.Failed; }
    }

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
