using System.Text.Json;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using Wino.AppServices.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.Uwp.Activation;
using Wino.Mail.Uwp.Services;
using Wino.Mail.Uwp.Theming;
using Wino.Mail.Uwp.Views;
using Wino.Messaging.UI;
using Wino.Services;
using Wino.Views;
using Wino.Views.Mail;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Core;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Core.Preview;
using Windows.UI.Notifications;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace Wino.Mail.Uwp;

public sealed partial class App : Application,
    IRecipient<AccountCreatedMessage>,
    IRecipient<AccountRemovedMessage>,
    IRecipient<WelcomeImportCompletedMessage>
{
    public new static App Current => (App)Application.Current;
    private ActivationInbox activationInbox = null!;
    private CompanionConnectionService companion = null!;
    private readonly UwpWindowPresentationManager windowPresentationManager = new();
    private ICompanionBackendControl backendControl = null!;
    private ServiceProvider services = null!;
    private ServerMessageBridge serverMessageBridge = null!;
    private ActivationPayloadRouter activationPayloadRouter = null!;
    private bool servicesInitialized;
    private bool applicationResourcesInitialized;
    private bool presentationInitialized;
    private long preferenceRevision;
    private readonly Windows.System.DispatcherQueue uiDispatcherQueue;
    private readonly SemaphoreSlim accountLifecycleSemaphore = new(1, 1);
    private Task? companionInitializationTask;
    private Task? startupWarmupTask;
    private Frame? startupFrame;

    public IServiceProvider Services => services;
    public INewThemeService NewThemeService => services.GetRequiredService<INewThemeService>();
    public IUnderlyingThemeService UnderlyingThemeService => services.GetRequiredService<IUnderlyingThemeService>();
    public UwpWindowPresentationManager WindowPresentationManager => windowPresentationManager;

    public App()
    {
        UwpDiagnostics.Write("Application instance constructed.");
        uiDispatcherQueue = Windows.System.DispatcherQueue.GetForCurrentThread();
        companion = new CompanionConnectionService(uiDispatcherQueue);
        companion.ShutdownFlushRequested += OnShutdownFlushRequested;
        UnhandledException += OnUnhandledException;
        try
        {
            InitializeComponent();
        }
        catch (Exception exception)
        {
            WriteCrashLog(exception);
            throw;
        }
        Suspending += OnSuspending;
        Resuming += OnResuming;

        WeakReferenceMessenger.Default.Register<AccountCreatedMessage>(this);
        WeakReferenceMessenger.Default.Register<AccountRemovedMessage>(this);
        WeakReferenceMessenger.Default.Register<WelcomeImportCompletedMessage>(this);
    }

    private void EnsureServicesInitialized()
    {
        if (servicesInitialized)
        {
            return;
        }

        activationInbox ??= new ActivationInbox();
        services = UwpContainerSetup.Build(companion, windowPresentationManager);
        backendControl = services.GetRequiredService<ICompanionBackendControl>();
        serverMessageBridge = new ServerMessageBridge(companion);
        activationPayloadRouter = new ActivationPayloadRouter(
            services.GetRequiredService<ILaunchProtocolService>(),
            services.GetRequiredService<IShareActivationService>(),
            services.GetRequiredService<IMailService>(),
            services.GetRequiredService<ICalendarService>());
        services.GetRequiredService<IPreferencesService>().PreferenceChanged += OnPreferenceChanged;
        servicesInitialized = true;

        // Translation and the read-only database are both required before the first
        // shell navigation. Start them immediately so the file and SQLite reads overlap
        // with companion launch and XAML resource loading instead of running serially.
        startupWarmupTask = WarmStartupServicesAsync();
    }

    private Task WarmStartupServicesAsync() => Task.WhenAll(
        services.GetRequiredService<ITranslationService>().InitializeAsync(),
        services.GetRequiredService<IDatabaseService>().InitializeAsync());

    private async void OnPreferenceChanged(object? sender, string propertyName)
    {
        try
        {
            await companion.ReloadPreferencesAsync(Interlocked.Increment(ref preferenceRevision));
        }
        catch
        {
            // Package settings are authoritative and remain visible to the companion.
            // Reconnect or the next preference change will refresh its live tray state.
        }
    }

    private static void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        WriteCrashLog(args.Exception);

        // Remote mutations can race a companion restart. Losing the optional
        // background process must never tear down the UWP window or welcome flow.
        if (args.Exception is WinoRemoteException { ErrorCode: RpcErrorCode.CompanionUnavailable })
        {
            args.Handled = true;
        }
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            var logPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "uwp-crash.log");
            File.AppendAllText(
                logPath,
                $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Last-resort diagnostics must never mask the original UI exception.
        }
    }

    [Conditional("DEBUG")]
    private static void TraceActivation(string message)
    {
        Debug.WriteLine($"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} {message}");
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        UwpDiagnostics.Write($"OnLaunched received. Arguments='{args.Arguments}'.");
        try
        {
            activationInbox ??= new ActivationInbox();
            EnsureApplicationResourcesInitialized();
            PrepareStartupSurface();
            EnsureServicesInitialized();
            await ActivateCanonicalWindowAsync(args);
        }
        catch (Exception exception)
        {
            ShowActivationFailure(exception);
        }
    }

    protected override async void OnActivated(IActivatedEventArgs args)
    {
        UwpDiagnostics.Write($"OnActivated received. Kind={args.Kind}.");
        try
        {
            activationInbox ??= new ActivationInbox();
            EnsureApplicationResourcesInitialized();
            PrepareStartupSurface();
            EnsureServicesInitialized();
            await ActivateCanonicalWindowAsync(args);
        }
        catch (Exception exception)
        {
            ShowActivationFailure(exception);
        }
    }

    private static void ShowActivationFailure(Exception exception)
    {
        WriteCrashLog(exception);
        Window.Current.Content = new ScrollViewer
        {
            Content = new TextBlock
            {
                Text = exception.ToString(),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24),
            },
        };
        Window.Current.Activate();
    }

    protected override async void OnBackgroundActivated(BackgroundActivatedEventArgs args)
    {
        base.OnBackgroundActivated(args);
        UwpDiagnostics.Write(
            $"OnBackgroundActivated received. " +
            $"Trigger={args.TaskInstance.TriggerDetails?.GetType().FullName ?? "null"}.");
        TraceActivation($"Background activation received. Trigger={args.TaskInstance.TriggerDetails?.GetType().FullName ?? "null"}");
        if (companion.OnBackgroundActivated(args))
        {
            UwpDiagnostics.Write("Companion AppService connection accepted.");
            TraceActivation("Companion AppService connection accepted.");
            return;
        }

        EnsureApplicationResourcesInitialized();
        EnsureServicesInitialized();
        if (args.TaskInstance.TriggerDetails is not ToastNotificationActionTriggerDetail toast)
        {
            return;
        }

        var deferral = args.TaskInstance.GetDeferral();
        try
        {
            var input = toast.UserInput.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.ToString() ?? string.Empty,
                StringComparer.Ordinal);
            var request = new BackgroundToastActionRequest(toast.Argument ?? string.Empty, input);
            if (!await companion.ConnectForBackgroundAsync())
            {
                return;
            }
            var response = await companion.InvokeAsync(
                "notification.background-action.v1",
                WinoAppServiceJsonContext.Serialize(request),
                CancellationToken.None);
            if (!response.IsSuccess)
            {
                throw new WinoRemoteException(response);
            }
        }
        finally
        {
            if (!companion.IsForegroundAttached)
            {
                try
                {
                    await companion.DetachAsync(ClientDetachReason.WindowClosed);
                }
                catch
                {
                    // A failed best-effort detach must not strand the background deferral.
                }
            }

            deferral.Complete();
        }
    }

    private async Task ActivateCanonicalWindowAsync(IActivatedEventArgs args)
    {
        TraceActivation($"Canonical activation started. Kind={args.Kind}");
        var rootFrame = Window.Current.Content as Frame ?? startupFrame;
        var isColdPresentation = rootFrame is null || ReferenceEquals(rootFrame, startupFrame);
        if (rootFrame is null)
        {
            rootFrame = new Frame();
            rootFrame.NavigationFailed += OnNavigationFailed;
        }

        if (isColdPresentation)
        {
            windowPresentationManager.Prepare(Window.Current, rootFrame);

            if (ApiInformation.IsTypePresent("Windows.UI.Core.Preview.SystemNavigationManagerPreview"))
            {
                SystemNavigationManagerPreview.GetForCurrentView().CloseRequested += OnCloseRequested;
            }
        }
        else
        {
            // A warm activation must foreground the page that is already rendered.
            Window.Current.Activate();
        }

        if (!presentationInitialized)
        {
            services.GetRequiredService<WinUIDispatcher>()
                .Initialize(Windows.System.DispatcherQueue.GetForCurrentThread());
        }

        var hasDirectActivation = args is not LaunchActivatedEventArgs launch ||
                                  !string.IsNullOrWhiteSpace(launch.Arguments);

        var database = services.GetRequiredService<IDatabaseService>();
        await (startupWarmupTask ?? WarmStartupServicesAsync());
        var accounts = database.IsAvailable
            ? await services.GetRequiredService<IAccountService>().GetAccountsAsync()
            : [];
        if (!accounts.Any())
        {
            ShowWelcomeFlow(rootFrame, resetWizard: rootFrame.Content is not WelcomeHostPage);
            await PresentRootFrameAsync(rootFrame, isColdPresentation);

            // Preserve protocol, file, toast, and share activations until account
            // setup creates the shell.
            if (hasDirectActivation)
            {
                await activationInbox.EnqueueAsync(args, ResolveTargetSurface(args));
                if (args is ShareTargetActivatedEventArgs share)
                {
                    share.ShareOperation.ReportCompleted();
                }
            }

            TraceActivation("No configured accounts. Showing the root welcome flow.");
            _ = StartPostPresentationServicesAsync();
            return;
        }

        EnsureShell(rootFrame);

        ActivationEnvelope? directEnvelope = null;
        if (hasDirectActivation)
        {
            directEnvelope = await activationInbox.EnqueueAsync(args, ResolveTargetSurface(args));
        }

        // Navigation is deliberately completed before companion startup. Every
        // account/folder/mail-list read used by this initial surface is served by the
        // UWP read-only SQLite connection.
        if (rootFrame.Content is WinoAppShell initialShell)
        {
            var initialMode = hasDirectActivation
                ? ResolveTargetSurface(args) == ActivationTargetSurface.Calendar
                    ? WinoApplicationMode.Calendar
                    : WinoApplicationMode.Mail
                : WinoApplicationMode.Mail;
            initialShell.ActivateMode(initialMode, new ShellModeActivationContext
            {
                IsInitialActivation = true,
            });
        }

        await PresentRootFrameAsync(rootFrame, isColdPresentation);

        _ = CompleteActivationAfterPresentationAsync(
            rootFrame,
            directEnvelope,
            args as ShareTargetActivatedEventArgs);
    }

    private async Task InitializeCompanionAsync()
    {
        try
        {
            await LaunchCompanionAsync();
            if (!await companion.ConnectAsync())
            {
                // The companion can crash during its own cold start (or the launch
                // request can get lost across a fast suspend/resume). One relaunch
                // covers that without looping a broken installation forever.
                await LaunchCompanionAsync();
                if (!await companion.ConnectAsync())
                {
                    companionInitializationTask = null;
                }
            }
        }
        catch (Exception exception)
        {
            // Local SQLite reads keep the shell usable. Clearing the cached task lets
            // the next StartCompanionAsync call (activation, resume, explicit mutation)
            // attempt a fresh launch instead of reusing this failure forever.
            WriteCrashLog(exception);
            companionInitializationTask = null;
        }
    }

    private Task StartCompanionAsync() =>
        companionInitializationTask ??= InitializeCompanionAsync();

    private async Task LaunchCompanionAsync()
    {
        if (!ApiInformation.IsApiContractPresent("Windows.ApplicationModel.FullTrustAppContract", 1, 0))
        {
            throw new PlatformNotSupportedException("The Wino companion requires the full-trust app contract.");
        }

        await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();
    }

    private void PrepareStartupSurface()
    {
        if (Window.Current.Content is not null || startupFrame is not null)
        {
            return;
        }

        var rootFrame = new Frame();
        rootFrame.NavigationFailed += OnNavigationFailed;
        windowPresentationManager.Prepare(Window.Current, rootFrame);
        startupFrame = rootFrame;
    }

    private void EnsureApplicationResourcesInitialized()
    {
        if (applicationResourcesInitialized)
        {
            return;
        }

        Resources.MergedDictionaries.Add(new AppResources());
        applicationResourcesInitialized = true;
    }

    private Task PresentRootFrameAsync(Frame rootFrame, bool isColdPresentation)
    {
        // Navigate while detached so the first UWP frame is real application UI, not
        // the themed background of an empty Frame. This also lets the system splash
        // remain in place until Welcome or the requested shell is ready to render.
        if (isColdPresentation)
        {
            TraceActivation($"Assigning navigated root frame. Content={rootFrame.Content?.GetType().FullName ?? "null"}");
            Window.Current.Content = rootFrame;
            startupFrame = null;
        }

        windowPresentationManager.ApplyRegisteredTitleBar();
        Window.Current.Activate();
        if (!presentationInitialized)
        {
            presentationInitialized = true;
            _ = services.GetRequiredService<INewThemeService>().InitializeAsync();
        }
        TraceActivation("Canonical window activated with navigated content.");
        return Task.CompletedTask;
    }

    private async Task CompleteActivationAfterPresentationAsync(
        Frame rootFrame,
        ActivationEnvelope? directEnvelope,
        ShareTargetActivatedEventArgs? shareActivation)
    {
        // Let Window.Activate return to the dispatcher so the compositor can present
        // the shell before full-trust launch or background-task registration begins.
        await Task.Yield();
        try
        {
            if (directEnvelope is not null)
            {
                await RouteAndCompleteAsync(directEnvelope, rootFrame);
            }

            await RoutePendingActivationsAsync(rootFrame);
            await StartPostPresentationServicesAsync();
        }
        catch (Exception exception)
        {
            WriteCrashLog(exception);
        }
        finally
        {
            shareActivation?.ShareOperation.ReportCompleted();
        }
    }

    private async Task StartPostPresentationServicesAsync()
    {
        await Task.Yield();
        _ = EnsureToastBackgroundRegistrationAsync();
        await StartCompanionAsync();
    }

    private void EnsureShell(Frame rootFrame)
    {
        if (rootFrame.Content is WinoAppShell)
        {
            return;
        }

        rootFrame.BackStack.Clear();
        rootFrame.ForwardStack.Clear();
        rootFrame.Navigate(typeof(WinoAppShell), null, new SuppressNavigationTransitionInfo());
        rootFrame.BackStack.Clear();
        rootFrame.ForwardStack.Clear();
    }

    private void ShowWelcomeFlow(Frame rootFrame, bool resetWizard)
    {
        if (resetWizard)
        {
            services.GetRequiredService<WelcomeWizardContext>().Reset();
        }

        var state = services.GetRequiredService<IStatePersistanceService>();
        state.AppModeTitle = string.Empty;
        state.CoreWindowTitle = string.Empty;

        if (rootFrame.Content is WelcomeHostPage welcomeHost)
        {
            if (resetWizard)
            {
                welcomeHost.ResetWizard();
            }

            return;
        }

        if (rootFrame.Content is WinoAppShell shell)
        {
            shell.PrepareForWindowClose();
        }

        rootFrame.BackStack.Clear();
        rootFrame.ForwardStack.Clear();
        rootFrame.Navigate(typeof(WelcomeHostPage), null, new SuppressNavigationTransitionInfo());
        rootFrame.BackStack.Clear();
        rootFrame.ForwardStack.Clear();
    }

    private async Task<bool> RoutePendingActivationsAsync(Frame rootFrame)
    {
        var routedAny = false;
        var pendingActivations = await activationInbox.ClaimPendingAsync();
        TraceActivation($"Pending activation count={pendingActivations.Count}");

        foreach (var envelope in pendingActivations)
        {
            await RouteAndCompleteAsync(envelope, rootFrame);
            routedAny = true;
        }

        return routedAny;
    }

    private async Task RouteAndCompleteAsync(ActivationEnvelope envelope, Frame rootFrame)
    {
        if (rootFrame.Content is WinoAppShell shell)
        {
            TraceActivation($"Routing envelope {envelope.Id} surface={envelope.TargetSurface} kind={envelope.Kind}");
            var parameter = await activationPayloadRouter.PrepareAsync(envelope);
            shell.ActivateMode(
                envelope.TargetSurface == ActivationTargetSurface.Calendar
                    ? WinoApplicationMode.Calendar
                    : WinoApplicationMode.Mail,
                new ShellModeActivationContext
                {
                    IsInitialActivation = false,
                    Parameter = parameter,
                });
            await activationInbox.CompleteAsync(envelope);
        }
    }

    public void Receive(AccountCreatedMessage message)
    {
        QueueAccountLifecycleTransition(async () =>
        {
            if (Window.Current.Content is not Frame { Content: WelcomeHostPage } rootFrame)
            {
                return;
            }

            await services.GetRequiredService<IDatabaseService>().InitializeAsync();

            EnsureShell(rootFrame);

            if (rootFrame.Content is WinoAppShell shell)
            {
                var targetMode = !message.Account.IsMailAccessGranted && message.Account.IsCalendarAccessGranted
                    ? WinoApplicationMode.Calendar
                    : WinoApplicationMode.Mail;

                shell.ActivateMode(targetMode, new ShellModeActivationContext
                {
                    IsInitialActivation = true,
                    SuppressStartupFlows = true,
                });
            }

            await RoutePendingActivationsAsync(rootFrame);
        });
    }

    public void Receive(WelcomeImportCompletedMessage message)
    {
        if (message.ImportedMailboxCount <= 0)
        {
            return;
        }

        QueueAccountLifecycleTransition(async () =>
        {
            if (Window.Current.Content is not Frame { Content: WelcomeHostPage } rootFrame)
            {
                return;
            }

            await services.GetRequiredService<IDatabaseService>().InitializeAsync();
            var accounts = await services.GetRequiredService<IAccountService>().GetAccountsAsync();
            if (!accounts.Any())
            {
                return;
            }

            EnsureShell(rootFrame);

            var firstAccount = accounts[0];
            var targetMode = !firstAccount.IsMailAccessGranted && firstAccount.IsCalendarAccessGranted
                ? WinoApplicationMode.Calendar
                : WinoApplicationMode.Mail;

            if (rootFrame.Content is WinoAppShell shell)
            {
                shell.ActivateMode(targetMode, new ShellModeActivationContext
                {
                    IsInitialActivation = true,
                    SuppressStartupFlows = true,
                });
            }

            await RoutePendingActivationsAsync(rootFrame);
        });
    }

    public void Receive(AccountRemovedMessage message)
    {
        QueueAccountLifecycleTransition(async () =>
        {
            if (Window.Current.Content is not Frame { Content: WinoAppShell } rootFrame)
            {
                // Account removals raised while rolling back the setup wizard must
                // not reset or recreate the welcome flow.
                return;
            }

            var accounts = await services.GetRequiredService<IAccountService>().GetAccountsAsync();
            if (accounts.Any())
            {
                return;
            }

            ShowWelcomeFlow(rootFrame, resetWizard: true);
        });
    }

    private void QueueAccountLifecycleTransition(Func<Task> transition)
    {
        if (!uiDispatcherQueue.TryEnqueue(async () =>
        {
            await accountLifecycleSemaphore.WaitAsync();
            try
            {
                await transition();
            }
            catch (Exception exception)
            {
                WriteCrashLog(exception);
            }
            finally
            {
                accountLifecycleSemaphore.Release();
            }
        }))
        {
            WriteCrashLog(new InvalidOperationException("The account lifecycle transition could not be queued on the UI thread."));
        }
    }

    private static ActivationTargetSurface ResolveTargetSurface(IActivatedEventArgs args)
    {
        if (args is ProtocolActivatedEventArgs protocol &&
            (protocol.Uri.Scheme.Equals("webcal", StringComparison.OrdinalIgnoreCase) ||
             protocol.Uri.Scheme.Equals("webcals", StringComparison.OrdinalIgnoreCase) ||
             protocol.Uri.Scheme.Equals("wino", StringComparison.OrdinalIgnoreCase) &&
             protocol.Uri.Host.Equals("calendar", StringComparison.OrdinalIgnoreCase)))
        {
            return ActivationTargetSurface.Calendar;
        }

        if (args is FileActivatedEventArgs file &&
            file.Files.OfType<StorageFile>().Any(item => item.FileType.Equals(".ics", StringComparison.OrdinalIgnoreCase)))
        {
            return ActivationTargetSurface.Calendar;
        }

        var arguments = args switch
        {
            LaunchActivatedEventArgs launch => launch.Arguments,
            ToastNotificationActivatedEventArgs toast => toast.Argument,
            _ => string.Empty,
        };

        return arguments?.Contains("calendar", StringComparison.OrdinalIgnoreCase) == true
            ? ActivationTargetSurface.Calendar
            : ActivationTargetSurface.Mail;
    }

    private async void OnCloseRequested(object? sender, SystemNavigationCloseRequestedPreviewEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            using (var draftCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
            {
                try
                {
                    await FlushActiveDraftAsync().WaitAsync(draftCancellation.Token);
                }
                catch (OperationCanceledException) when (draftCancellation.IsCancellationRequested)
                {
                    // Window close remains bounded if WebView2 does not return editor content.
                }
            }

            var closeBehavior = ApplicationData.Current.LocalSettings.Values.TryGetValue("AppCloseBehavior", out var value)
                ? value?.ToString()
                : null;

            if (string.Equals(closeBehavior, "Terminate", StringComparison.Ordinal))
            {
                using var flushCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await backendControl.FlushAsync(flushCancellation.Token);
                }
                catch (OperationCanceledException) when (flushCancellation.IsCancellationRequested)
                {
                    // Closing the window remains bounded if a provider does not finish promptly.
                }

                try
                {
                    await companion.ShutdownAsync(CancellationToken.None);
                }
                catch
                {
                    // The companion may close its AppService connection immediately after
                    // acknowledging shutdown. The UWP window must still be allowed to exit.
                }

                await companion.DetachAsync(ClientDetachReason.Terminating);
            }
            else
            {
                await companion.DetachAsync(ClientDetachReason.WindowClosed);
            }
        }
        finally
        {
            companion.OnCloseRequested(args);
            deferral.Complete();
        }
    }

    private async void OnShutdownFlushRequested(ShutdownFlushRequest request)
    {
        var remaining = new DateTimeOffset(request.DeadlineUtcTicks, TimeSpan.Zero) - DateTimeOffset.UtcNow;
        using var cancellation = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        try
        {
            await FlushActiveDraftAsync().WaitAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The companion owns the deadline and will continue its bounded shutdown.
        }
        catch (Exception)
        {
            // A failed draft save must still release the companion's shutdown wait.
        }
        finally
        {
            try
            {
                await companion.CompleteShutdownFlushAsync(request.RequestId, CancellationToken.None);
            }
            catch
            {
                // The companion can reach its deadline and terminate before this acknowledgement.
            }

            if (request.ExitUi)
            {
                await companion.DetachAsync(ClientDetachReason.Terminating);
                CoreApplication.Exit();
            }
        }
    }

    private static Task FlushActiveDraftAsync()
    {
        if (Window.Current.Content is Frame { Content: WinoAppShell shell } &&
            shell.GetFrame(NavigationReferenceFrame.RenderingFrame)?.Content is ComposePage composePage)
        {
            return composePage.FlushDraftAsync();
        }

        return Task.CompletedTask;
    }

    private static void OnNavigationFailed(object sender, NavigationFailedEventArgs args) =>
        throw new InvalidOperationException($"Failed to load page '{args.SourcePageType.FullName}'.");

    private async void OnSuspending(object sender, SuspendingEventArgs args)
    {
        var deferral = args.SuspendingOperation.GetDeferral();
        try
        {
            await companion.DetachAsync(ClientDetachReason.Suspended);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnResuming(object? sender, object args)
    {
        try
        {
            if (!await companion.ConnectAsync())
            {
                await LaunchCompanionAsync();
                _ = await companion.ConnectAsync();
            }
        }
        catch
        {
            // The next foreground operation will make another bounded connection attempt.
            // Resume must not crash the UI when the companion is restarting.
        }
    }

    private static async Task EnsureToastBackgroundRegistrationAsync()
    {
        try
        {
            await ToastBackgroundRegistration.EnsureRegisteredAsync();
        }
        catch
        {
            // Background actions are optional when system policy denies registration.
            // Foreground toast activation and the companion remain available.
        }
    }
}
