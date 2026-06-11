using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Wino.BackgroundService.Services;
using Wino.BackgroundService.Tray;
using Wino.Core;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Messaging;
using Wino.Core.Services;
using Wino.Ipc;
using Wino.Ipc.Contracts;
using Wino.Ipc.Contracts.Generated;
using Wino.Ipc.Transport;
using Wino.Services;

namespace Wino.BackgroundService;

/// <summary>
/// Headless Win32 companion process: owns the SQLite database, the synchronization and
/// reminder loops and the tray icon, and serves the UI process over a named pipe.
/// </summary>
public static class Program
{
    public const int IpcProtocolVersion = 1;
    private const string SingleInstanceMutexName = @"Local\WinoBackgroundServiceRunning";

    private static int _terminationRequested;
    private static uint _mainThreadId;

    [MTAThread]
    public static int Main(string[] args)
    {
        using var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);

        var activationArguments = GetActivationArguments(args);

        if (!isFirstInstance)
        {
            // Forward activation (e.g. toast clicks) to the running instance and exit.
            ForwardToRunningInstanceAsync(activationArguments).GetAwaiter().GetResult();
            return 0;
        }

        try
        {
            return RunAsync(activationArguments).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Background service crashed during startup.");
            Log.CloseAndFlush();
            return 1;
        }
    }

    private static async Task<int> RunAsync(string? activationArguments)
    {
        _mainThreadId = GetCurrentThreadId();

        // Source-generated serialization for all RPC payloads; required before any pipe traffic.
        WinoIpcJson.Initialize(Wino.Ipc.Serialization.WinoIpcJsonContext.Default);

        // S/MIME cryptography runs exclusively in this process (see ISmimeService);
        // the context is needed for decrypt/verify and recipient certificate lookups.
        MimeKit.Cryptography.CryptographyContext.Register(typeof(MimeKit.Cryptography.WindowsSecureMimeContext));

        ConfigureBootstrapLogging();

        Log.Information("Wino background service starting. Args: {Args}", activationArguments ?? "<none>");

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Fatal(e.ExceptionObject as Exception, "AppDomain unhandled exception.");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error(e.Exception, "Unobserved task exception."); e.SetObserved(); };

        var services = BuildServiceProvider();

        // Paths must be set before anything touches the database or token caches.
        var appConfiguration = services.GetRequiredService<IApplicationConfiguration>();
        appConfiguration.ApplicationDataFolderPath = ApplicationData.Current.LocalFolder.Path;
        appConfiguration.PublisherSharedFolderPath = ApplicationData.Current.GetPublisherCacheFolder(ApplicationConfiguration.SharedFolderName).Path;
        appConfiguration.ApplicationTempFolderPath = ApplicationData.Current.TemporaryFolder.Path;

        var logger = services.GetRequiredService<IWinoLogger>();
        logger.SetupLogger(Path.Combine(ApplicationData.Current.LocalFolder.Path, "WinoBackgroundService.log"));

        // RPC server. It starts listening BEFORE the database and synchronization manager
        // initialize so a launching UI can connect immediately; requests received in the
        // meantime wait on the initialization gate instead of timing out the client.
        var nativeAppService = services.GetRequiredService<INativeAppService>();
        var pipeName = PipeNaming.GetPipeName(Package.Current.Id.FamilyName, Process.GetCurrentProcess().SessionId);

        var toastActionHandler = services.GetRequiredService<ToastActionHandler>();
        var backgroundServiceControl = new BackgroundServiceControl(toastActionHandler, nativeAppService, services.GetRequiredService<IPreferencesService>(), Terminate);

        var initializationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var dispatcher = new WinoRpcDispatcher(
            services.GetRequiredService<IAccountService>(),
            backgroundServiceControl,
            services.GetRequiredService<ICalendarService>(),
            services.GetRequiredService<IContactPictureFileService>(),
            services.GetRequiredService<IContactService>(),
            services.GetRequiredService<IEmailTemplateService>(),
            services.GetRequiredService<IFolderService>(),
            services.GetRequiredService<IKeyboardShortcutService>(),
            services.GetRequiredService<IMailCategoryService>(),
            services.GetRequiredService<IMailRenderService>(),
            services.GetRequiredService<IMailService>(),
            services.GetRequiredService<ISentMailReceiptService>(),
            services.GetRequiredService<ISignatureService>(),
            services.GetRequiredService<ISmimeService>(),
            services.GetRequiredService<ISynchronizationManager>(),
            services.GetRequiredService<IThumbnailCacheService>(),
            services.GetRequiredService<IWinoAccountDataSyncService>(),
            services.GetRequiredService<IWinoAccountProfileService>(),
            services.GetRequiredService<IWinoRequestDelegator>());

        var serverHost = new NamedPipeRpcServerHost(
            pipeName,
            new InitializationGateHandler(initializationCompletion.Task, dispatcher),
            new RpcServerConnectionOptions
            {
                ProtocolVersion = IpcProtocolVersion,
                AppVersion = nativeAppService.GetFullAppVersion(),
                ExceptionMapper = WinoRpcDomainExceptions.ToErrorEnvelope,
                OperationDeduplicator = new RpcOperationDeduplicator(),
            });

        // UI messages: publish locally + forward to connected clients.
        UIMessagePublisherProvider.Current = new PipeUIMessagePublisher(serverHost);

        serverHost.Start();
        Log.Information("RPC server listening on pipe {PipeName}.", pipeName);

        // Database, translations and the synchronization manager; opens the gate when done.
        try
        {
            await services.GetRequiredService<IDatabaseService>().InitializeAsync().ConfigureAwait(false);
            await services.GetRequiredService<ITranslationService>().InitializeAsync().ConfigureAwait(false);
            await services.GetRequiredService<SynchronizationManagerInitializer>().InitializeAsync().ConfigureAwait(false);

            initializationCompletion.TrySetResult();
            Log.Information("Background service initialization completed; requests are now served.");
        }
        catch (Exception initializationException)
        {
            initializationCompletion.TrySetException(initializationException);
            throw;
        }

        // Lifecycle and background loops.
        var preferencesService = services.GetRequiredService<IPreferencesService>();
        var accountService = services.GetRequiredService<IAccountService>();

        using var lifecycle = new CompanionLifecycle(serverHost, preferencesService, accountService, Terminate);
        await lifecycle.EvaluateStartupAsync().ConfigureAwait(false);

        // Bridges queued-action sync requests (delegator, IMAP idle) to the sync manager.
        var syncRequestForwarder = new SyncRequestForwarder(
            services.GetRequiredService<ISynchronizationManager>(),
            services.GetRequiredService<IAccountService>());

        using var synchronizationLoop = services.GetRequiredService<SynchronizationLoopService>();
        synchronizationLoop.Start();

        using var reminderLoop = services.GetRequiredService<CalendarReminderLoop>();
        await reminderLoop.StartAsync().ConfigureAwait(false);

        // Handle the activation that launched us (toast click on a cold companion).
        if (!string.IsNullOrWhiteSpace(activationArguments))
        {
            _ = toastActionHandler.HandleAsync(activationArguments);
        }

        // Tray icon + message loop on a dedicated STA-like thread is unnecessary;
        // the message-only window works on the main thread message pump.
        NativeTrayIcon? trayIcon = null;

        if (preferencesService.AppCloseBehavior == AppCloseBehavior.RunInBackgroundWithTrayIcon)
        {
            trayIcon = CreateTrayIcon(services);
        }

        preferencesService.PreferenceChanged += (_, propertyName) =>
        {
            if (propertyName != nameof(IPreferencesService.AppCloseBehavior))
                return;

            if (preferencesService.AppCloseBehavior == AppCloseBehavior.RunInBackgroundWithTrayIcon)
            {
                trayIcon ??= CreateTrayIcon(services);
            }
            else
            {
                trayIcon?.Dispose();
                trayIcon = null;
            }
        };

        Log.Information("Background service is serving.");

        RunMessageLoop();

        Log.Information("Background service message loop ended; shutting down.");

        // The messenger holds weak references; keep the forwarder alive for the process lifetime.
        GC.KeepAlive(syncRequestForwarder);

        trayIcon?.Dispose();
        await serverHost.DisposeAsync().ConfigureAwait(false);
        Log.CloseAndFlush();

        return 0;
    }

    private static IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.RegisterCoreServices();
        services.RegisterSharedServices();

        // Companion-specific overrides (last registration wins).
        services.AddSingleton<IAuthenticatorConfig, MailAuthenticatorConfiguration>();
        services.AddTransient<IKeyPressService, HeadlessKeyPressService>();
        services.AddSingleton<IStoreManagementService, CompanionStoreManagementService>();
        services.AddSingleton<IMailDialogService, HeadlessMailDialogService>();
        services.AddSingleton<HeadlessNativeAppService>();
        services.AddSingleton<INativeAppService>(provider => provider.GetRequiredService<HeadlessNativeAppService>());
        services.AddSingleton<IAppMetadataService>(provider => provider.GetRequiredService<HeadlessNativeAppService>());
        services.AddTransient<IConfigurationService, CompanionConfigurationService>();
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddTransient<INotificationBuilder, CompanionNotificationBuilder>();

        services.AddSingleton<PackagedAppEntryLauncher>();
        services.AddSingleton<ToastActionHandler>();
        services.AddSingleton<SynchronizationLoopService>();
        services.AddSingleton<CalendarReminderLoop>();

        return services.BuildServiceProvider();
    }

    private static NativeTrayIcon? CreateTrayIcon(IServiceProvider services)
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Wino_Icon.ico");

            if (!File.Exists(iconPath))
            {
                // WAP layout: each project lives in its own subfolder of the package root.
                iconPath = Path.Combine(Package.Current.InstalledLocation.Path, "Wino.Mail.WinUI", "Assets", "Wino_Icon.ico");
            }

            if (!File.Exists(iconPath))
            {
                Log.Warning("Tray icon file was not found; tray icon is disabled.");
                return null;
            }

            var launcher = services.GetRequiredService<PackagedAppEntryLauncher>();

            var trayIcon = new NativeTrayIcon(
                action => _ = Task.Run(action),
                iconPath,
                "Wino Mail",
                () => BuildTrayMenu(launcher),
                launcher.LaunchMailAsync);

            trayIcon.Create();
            Log.Information("Companion tray icon created.");
            return trayIcon;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to create companion tray icon.");
            return null;
        }
    }

    private static IReadOnlyList<NativeTrayIcon.NativeTrayMenuItem> BuildTrayMenu(PackagedAppEntryLauncher launcher) =>
    [
        new(Translator.SystemTrayMenu_Open, launcher.LaunchMailAsync, IsDefault: true),
        new(Translator.SystemTrayMenu_ShowWino, launcher.LaunchMailAsync),
        new(Translator.SystemTrayMenu_ShowWinoCalendar, launcher.LaunchCalendarAsync),
        NativeTrayIcon.NativeTrayMenuItem.Separator(),
        new(Translator.SystemTrayMenu_ExitWino, () => { Terminate(); return Task.CompletedTask; }),
    ];

    /// <summary>
    /// Resolves the activation argument string: explicit command line arguments, or the
    /// toast activation payload when the packaged app was activated from a notification.
    /// </summary>
    private static string? GetActivationArguments(string[] args)
    {
        try
        {
            if (AppInstance.GetActivatedEventArgs() is ToastNotificationActivatedEventArgs toastArgs &&
                !string.IsNullOrWhiteSpace(toastArgs.Argument))
            {
                return toastArgs.Argument;
            }
        }
        catch
        {
            // Not activated through the packaged activation pipeline (e.g. plain exe launch).
        }

        return args.Length > 0 ? string.Join(' ', args) : null;
    }

    private static async Task ForwardToRunningInstanceAsync(string? activationArguments)
    {
        if (string.IsNullOrWhiteSpace(activationArguments))
            return;

        try
        {
            var pipeName = PipeNaming.GetPipeName(Package.Current.Id.FamilyName, Process.GetCurrentProcess().SessionId);
            var stream = await NamedPipeTransport.ConnectAsync(pipeName, TimeSpan.FromSeconds(3)).ConfigureAwait(false);

            await using var client = new RpcClient(stream);
            var handshake = await client.HandshakeAsync(new Ipc.Protocol.HandshakeRequest(IpcProtocolVersion, "secondary-instance", "WinoBackgroundService"))
                .ConfigureAwait(false);

            if (handshake.Accepted)
            {
                var control = new BackgroundServiceControlRemoteProxy(client);
                await control.HandleToastActionsAsync(activationArguments).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to forward activation to the running companion instance.");
        }
    }

    private static void Terminate()
    {
        if (Interlocked.Exchange(ref _terminationRequested, 1) == 1)
            return;

        // WM_QUIT must land on the main thread's message queue regardless of the caller.
        const uint WmQuit = 0x0012;
        PostThreadMessageW(_mainThreadId, WmQuit, 0, 0);
    }

    #region Win32 message loop

    private static void RunMessageLoop()
    {
        // Classic GetMessage pump: required by the tray's message-only window and keeps
        // the main thread alive while all real work happens on the thread pool.
        while (GetMessageW(out var message, nint.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessageW(out MSG message, nint hwnd, uint messageFilterMin, uint messageFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref MSG message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    #endregion

    private static void ConfigureBootstrapLogging()
    {
        // Minimal logger until IWinoLogger reconfigures sinks with user preferences.
        try
        {
            var logPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "WinoBackgroundService.log");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(logPath, shared: true, retainedFileCountLimit: 2, fileSizeLimitBytes: 10 * 1024 * 1024)
                .CreateLogger();
        }
        catch
        {
            Log.Logger = new LoggerConfiguration().CreateLogger();
        }
    }
}
