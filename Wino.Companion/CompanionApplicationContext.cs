using System.Windows.Forms;
using Wino.AppServices.Contracts;
using Wino.Companion.Backend;
using Wino.Companion.Services;
using Wino.Companion.Tray;
using Windows.ApplicationModel;

namespace Wino.Companion;

internal sealed class CompanionApplicationContext : ApplicationContext
{
    private readonly CompanionSingleInstance singleInstance;
    private readonly Control dispatcher = new();
    private readonly SemaphoreSlim connectionGate = new(1, 1);

    private CompanionAppService? appService;
    private CompanionBackendHost? backendHost;
    private CompanionMessengerBridge? messengerBridge;
    private CompanionCommandBridge? commandBridge;
    private CompanionLifecycleCoordinator? lifecycle;
    private CompanionTrayIcon? trayIcon;
    private CompanionPreferences? preferences;
    private PackagedAppEntryLauncher? launcher;
    private CompanionShutdownCoordinator? shutdown;
    private int exiting;

    public CompanionApplicationContext(CompanionSingleInstance singleInstance)
    {
        this.singleInstance = singleInstance;
        dispatcher.CreateControl();

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        singleInstance.ActivationRequested += OnActivationRequested;

        _ = InitializeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            singleInstance.ActivationRequested -= OnActivationRequested;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            lifecycle?.Dispose();
            trayIcon?.Dispose();
            messengerBridge?.Dispose();
            commandBridge?.Dispose();
            appService?.Dispose();
            if (backendHost is not null)
            {
                backendHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            connectionGate.Dispose();
            dispatcher.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task InitializeAsync()
    {
        try
        {
            CompanionDiagnostics.Write("Companion initialization started.");
            appService = new CompanionAppService();
            appService.Prepare();
            messengerBridge = new CompanionMessengerBridge(appService);

            var windowHandleProvider = new CompanionWindowHandleProvider(appService);
            backendHost = new CompanionBackendHost(windowHandleProvider.GetWindowHandle);
            appService.Methods.RegisterGeneratedDispatcher(
                backendHost.Dispatcher,
                backendHost.WaitUntilReadyAsync);
            RegisterNotificationHandlers(appService, backendHost);

            // Register the complete RPC table before opening the transport. This
            // keeps connection establishment early without exposing a connection
            // that temporarily reports generated methods as unsupported. Requests
            // that need the backend wait on its readiness gate instead.
            await ConnectOnceAsync().ConfigureAwait(false);

            CompanionDiagnostics.Write("Companion backend initialization started.");
            await backendHost.InitializeAsync().ConfigureAwait(false);
            CompanionDiagnostics.Write("Companion backend initialization completed.");
            commandBridge = new CompanionCommandBridge(backendHost.SynchronizationManager);

            preferences = new CompanionPreferences();
            launcher = new PackagedAppEntryLauncher();
            shutdown = new CompanionShutdownCoordinator(appService, backendHost.Control);
            shutdown.RegisterRpcHandler();

            appService.ShutdownRequested += OnShutdownRequested;
            appService.PreferencesReloadRequested += OnPreferencesReloadRequested;
            lifecycle = new CompanionLifecycleCoordinator(
                appService,
                backendHost.Control,
                preferences,
                RequestExit);

            RunOnUiThread(UpdateTray);
            CompanionDiagnostics.Write("Companion initialization completed.");
        }
        catch (Exception exception)
        {
            CompanionDiagnostics.Write("Companion initialization failed.", exception);
            WriteCrashLog(exception);
            RequestExit();
        }
    }

    private async Task ConnectOnceAsync()
    {
        if (appService is null || Volatile.Read(ref exiting) != 0)
        {
            return;
        }

        await connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // The UWP client can still be inside its own activation when the companion
            // starts, which makes OpenAsync fail transiently. Retry with backoff so the
            // very first UI request does not observe a missing connection.
            const int maxAttempts = 5;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (Volatile.Read(ref exiting) != 0)
                {
                    return;
                }

                try
                {
                    CompanionDiagnostics.Write($"Opening the UWP AppService connection (attempt {attempt}/{maxAttempts}).");
                    await appService.InitializeAppServiceAsync().ConfigureAwait(false);
                    CompanionDiagnostics.Write("The UWP AppService connection is open.");
                    return;
                }
                catch (Exception exception)
                {
                    CompanionDiagnostics.Write("Opening the UWP AppService connection failed.", exception);
                    if (attempt == maxAttempts)
                    {
                        WriteCrashLog(exception);
                        return;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250 << (attempt - 1))).ConfigureAwait(false);
            }
        }
        finally
        {
            connectionGate.Release();
        }
    }

    private void OnActivationRequested(object? sender, EventArgs eventArgs) => _ = ConnectOnceAsync();

    private void OnPreferencesReloadRequested(object? sender, EventArgs eventArgs) => RunOnUiThread(UpdateTray);

    private void OnShutdownRequested(object? sender, EventArgs eventArgs) => RequestExit();

    private void UpdateTray()
    {
        if (preferences is null || launcher is null || shutdown is null)
        {
            return;
        }

        if (preferences.CloseBehavior != CompanionCloseBehavior.RunInBackgroundWithTrayIcon)
        {
            trayIcon?.Dispose();
            trayIcon = null;
            return;
        }

        trayIcon ??= new CompanionTrayIcon(
            GetTrayIconPath(),
            launcher.LaunchMailAsync,
            launcher.LaunchCalendarAsync,
            () => shutdown.RequestExitAsync(RequestExit));
    }

    private static string GetTrayIconPath()
    {
        try
        {
            var packagedIcon = Path.Combine(Package.Current.InstalledLocation.Path, "Assets", "Wino_Icon.ico");
            if (File.Exists(packagedIcon))
            {
                return packagedIcon;
            }
        }
        catch
        {
            // An unpackaged developer launch falls back to the executable output.
        }

        return Path.Combine(AppContext.BaseDirectory, "Assets", "Wino_Icon.ico");
    }

    private void RequestExit()
    {
        if (Interlocked.Exchange(ref exiting, 1) != 0)
        {
            return;
        }

        RunOnUiThread(ExitThread);
    }

    private void RunOnUiThread(Action action)
    {
        if (dispatcher.IsDisposed)
        {
            return;
        }

        if (dispatcher.InvokeRequired)
        {
            dispatcher.BeginInvoke(action);
            return;
        }

        action();
    }

    private static void RegisterNotificationHandlers(CompanionAppService service, CompanionBackendHost backend)
    {
        // Handlers are registered before the backend finishes initializing so the RPC
        // table is complete when the transport opens. Each dispatch waits for readiness.
        void Register(string methodId, Func<RpcRequest, CancellationToken, Task<RpcResponse>> handler) =>
            service.Methods.Register(methodId, async (request, cancellationToken) =>
            {
                await backend.WaitUntilReadyAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                    .ConfigureAwait(false);
                return await handler(request, cancellationToken).ConfigureAwait(false);
            });

        Register("notification.background-action.v1", async (request, cancellationToken) =>
        {
            var action = DeserializeRequired(
                request.Payload,
                WinoAppServiceJsonContext.Default.BackgroundToastActionRequest);
            await backend.HandleToastActionAsync(action, cancellationToken).ConfigureAwait(false);
            return RpcResponse.Success();
        });
        Register("notification.update-mail-badge.v1", async (request, cancellationToken) =>
        {
            await backend.Notifications.UpdateTaskbarIconBadgeAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            return RpcResponse.Success();
        });
        Register("notification.update-jumplist.v1", async (request, cancellationToken) =>
        {
            await backend.Notifications.UpdateJumpListOptionsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            return RpcResponse.Success();
        });
        Register("notification.clear-calendar-badge.v1", async (request, cancellationToken) =>
        {
            await backend.Notifications.ClearCalendarTaskbarBadgeAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            return RpcResponse.Success();
        });
        Register("notification.add-calendar-badge.v1", async (request, cancellationToken) =>
        {
            var badge = DeserializeRequired(
                request.Payload,
                WinoAppServiceJsonContext.Default.BadgeCountRequest);
            await backend.Notifications.AddCalendarTaskbarBadgeCountAsync(badge.Count)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return RpcResponse.Success();
        });
        Register("notification.remove-mail.v1", (request, _) =>
        {
            var notification = DeserializeRequired(
                request.Payload,
                WinoAppServiceJsonContext.Default.RemoveMailNotificationRequest);
            backend.Notifications.RemoveNotification(notification.MailId);
            return Task.FromResult(RpcResponse.Success());
        });
        Register("notification.account-attention.v1", async (request, cancellationToken) =>
        {
            await backend.CreateAccountAttentionNotificationAsync(
                    DeserializeRequired(request.Payload, WinoAppServiceJsonContext.Default.AccountAttentionNotificationRequest),
                    cancellationToken)
                .ConfigureAwait(false);
            return RpcResponse.Success();
        });
        Register("notification.create-mail.v1", async (request, cancellationToken) =>
        {
            await backend.CreateMailNotificationsAsync(
                    DeserializeRequired(request.Payload, WinoAppServiceJsonContext.Default.MailNotificationsRequest),
                    cancellationToken)
                .ConfigureAwait(false);
            return RpcResponse.Success();
        });
        Register("notification.calendar-reminder.v1", async (request, cancellationToken) =>
        {
            await backend.CreateCalendarReminderNotificationAsync(
                    DeserializeRequired(request.Payload, WinoAppServiceJsonContext.Default.CalendarReminderNotificationRequest),
                    cancellationToken)
                .ConfigureAwait(false);
            return RpcResponse.Success();
        });
    }

    private static T DeserializeRequired<T>(string? payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        string.IsNullOrWhiteSpace(payload)
            ? throw new InvalidOperationException($"A {typeof(T).Name} payload is required.")
            : System.Text.Json.JsonSerializer.Deserialize(payload, typeInfo)
              ?? throw new InvalidOperationException($"The {typeof(T).Name} payload was null.");

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs) =>
        WriteCrashLog(eventArgs.ExceptionObject as Exception);

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs eventArgs)
    {
        WriteCrashLog(eventArgs.Exception);
        eventArgs.SetObserved();
    }

    private static void WriteCrashLog(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            var directory = Path.Combine(CompanionPaths.LocalData, "Companion");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "companion-crash.log"),
                $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

}
