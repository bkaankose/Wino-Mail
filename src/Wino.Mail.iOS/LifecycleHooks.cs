using BackgroundTasks;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Apple.Services;

namespace Wino.Mail.iOS;

/// <summary>
/// Notification categories, the response delegate's services, calendar reminders and the badge (Mac
/// AppDelegate.Notifications). The delegate itself is installed before launching finishes so a cold-launch tap is kept.
/// </summary>
internal sealed class NotificationsLifecycleHook(IServiceProvider services, AppleNotificationResponseHandler responses) : IApplicationLifecycleHook
{
    public async Task ServicesReadyAsync()
    {
        var builder = services.GetRequiredService<INotificationBuilder>();
        // Categories exist before any request so actions show on the first notification.
        (builder as AppleNotificationBuilder)?.RegisterCategories();
        responses.Attach(services);
        await services.GetRequiredService<ICalendarReminderServer>().StartAsync();
        await builder.UpdateTaskbarIconBadgeAsync();
    }

    public async Task StoppingAsync(bool runtimeStarted)
    {
        responses.Detach();
        if (runtimeStarted) await services.GetRequiredService<ICalendarReminderServer>().StopAsync();
    }
}

/// <summary>Routes the URLs and files that arrived while the app was launching.</summary>
internal sealed class ActivationLifecycleHook(IServiceProvider services) : IApplicationLifecycleHook
{
    public Task ServicesReadyAsync() => services.GetRequiredService<UIKitActivationCoordinator>().SetReady();

    public Task StoppingAsync(bool runtimeStarted) => Task.CompletedTask;
}

/// <summary>
/// Background refresh: BGTaskScheduler wakes the app and the shared runtime runs one bounded synchronization
/// (<see cref="IApplicationRuntime.SynchronizeOnceAsync"/>). The handlers are registered before launching finishes
/// (<see cref="Register"/>); requests are submitted once services are ready and whenever the scene goes to the background.
/// </summary>
internal sealed class BackgroundRefreshLifecycleHook(IServiceProvider services) : IApplicationLifecycleHook
{
    public const string RefreshTaskId = "com.winomail.macos.refresh";
    public const string ProcessingTaskId = "com.winomail.macos.processing";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    private static Func<IApplicationRuntime?>? _runtime;
    private static Func<IPushRelayRegistrationService?>? _pushRelay;

    public Task ServicesReadyAsync()
    {
        _runtime = () => services.GetService<IApplicationRuntime>();
        _pushRelay = () => services.GetService<IPushRelayRegistrationService>();
        Schedule();
        return Task.CompletedTask;
    }

    public Task StoppingAsync(bool runtimeStarted)
    {
        _runtime = null;
        _pushRelay = null;
        return Task.CompletedTask;
    }

    /// <summary>Registers the task handlers. Must run before application:didFinishLaunchingWithOptions: returns.</summary>
    public static void Register()
    {
        BGTaskScheduler.Shared.Register(RefreshTaskId, null, task => Run(task, reschedule: true));
        BGTaskScheduler.Shared.Register(ProcessingTaskId, null, task => Run(task, reschedule: false));
    }

    /// <summary>Asks for the next refresh; the system decides when it runs.</summary>
    public static void Schedule()
    {
        var request = new BGAppRefreshTaskRequest(RefreshTaskId) { EarliestBeginDate = (NSDate)DateTime.UtcNow.Add(RefreshInterval) };
        if (OperatingSystem.IsIOSVersionAtLeast(27))
            BGTaskScheduler.Shared.Submit(request, error => NotScheduled(error));
        else if (!BGTaskScheduler.Shared.Submit(request, out var error))
            NotScheduled(error);
    }

    private static void NotScheduled(NSError? error)
    {
        if (error is not null) Serilog.Log.Information("Background refresh was not scheduled: {Error}", error.LocalizedDescription);
    }

    private static async void Run(BGTask task, bool reschedule)
    {
        if (reschedule) Schedule();
        using var cancellation = new CancellationTokenSource();
        task.ExpirationHandler = cancellation.Cancel;
        var success = false;
        try
        {
            if (_runtime?.Invoke() is { } runtime)
            {
                await runtime.SynchronizeOnceAsync(cancellation.Token);
                success = !cancellation.IsCancellationRequested;
            }
            // The daily relay check-in keeps the device alive on WinoRelay when no push woke the app for a while.
            if (!cancellation.IsCancellationRequested) await CheckInAsync(cancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Serilog.Log.Warning(exception, "Background synchronization failed."); }
        finally { task.SetTaskCompleted(success); }
    }

    /// <summary>
    /// Refreshes the relay registration when a scene returns to the foreground. Ignored until services are ready:
    /// sceneWillEnterForeground also fires during launch, before the database is open.
    /// </summary>
    public static void RequestForegroundRelaySync()
        => _pushRelay?.Invoke()?.RequestSync(PushRelaySyncReason.Foreground);

    private static async Task CheckInAsync(CancellationToken cancellationToken)
    {
        if (_pushRelay?.Invoke() is not { } pushRelay) return;
        try { await pushRelay.CheckInAsync(cancellationToken); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Serilog.Log.Warning(exception, "Relay check-in during background refresh failed."); }
    }
}

#if WINO_APPSTORE
/// <summary>
/// App Store purchases (Mac AppDelegate.AppStore): listens for renewals and other transactions that arrive outside a
/// purchase, and links the purchases made for the signed-in Wino Account, retrying links that failed earlier. The sync
/// runs in the background so startup does not wait for StoreKit or the network.
/// </summary>
internal sealed class AppStoreLifecycleHook(IServiceProvider services) : IApplicationLifecycleHook
{
    public Task ServicesReadyAsync()
    {
        if (services.GetService<IWinoAppStorePurchaseService>() is not { IsAvailable: true } purchases) return Task.CompletedTask;
        purchases.StartTransactionUpdates();
        _ = SyncAsync(purchases);
        return Task.CompletedTask;
    }

    public Task StoppingAsync(bool runtimeStarted) => Task.CompletedTask;

    private static async Task SyncAsync(IWinoAppStorePurchaseService purchases)
    {
        try { await purchases.SyncOwnedPurchasesAsync(); }
        catch (Exception exception) { Serilog.Log.Warning(exception, "Syncing App Store purchases failed."); }
    }
}
#endif

#if DEBUG
/// <summary>Starts the file-command debug bridge (snapshots for remote development in the Simulator).</summary>
internal sealed class DebugBridgeLifecycleHook(IServiceProvider services, IDispatcher dispatcher) : IApplicationLifecycleHook
{
    public Task ServicesReadyAsync() => dispatcher.ExecuteOnUIThread(() => UIKitDebugBridge.Start(services));

    public Task StoppingAsync(bool runtimeStarted) => Task.CompletedTask;
}
#endif
