using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using MimeKit.Cryptography;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Common;
using Wino.Core.Domain.Models.Launch;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.ViewModels;
using Wino.Mail.Services;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Activation;
using Wino.Mail.WinUI.Extensions;
using Wino.Mail.WinUI.Helpers;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Mail.WinUI.Services;
using Wino.Mail.WinUI.ViewModels;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.Server;
using Wino.Messaging.UI;
using Wino.Services;
using Wino.Views;
using WinUIEx;
namespace Wino.Mail.WinUI;

public partial class App : WinoApplication,
    IRecipient<NewMailSynchronizationRequested>,
    IRecipient<NewCalendarSynchronizationRequested>,
    IRecipient<AccountCreatedMessage>,
    IRecipient<AccountRemovedMessage>,
    IRecipient<GetStartedFromWelcomeRequested>,
    IRecipient<WelcomeImportCompletedMessage>
{
    private const int InboxSyncsPerFullSync = 20;
    private const string ToggleDefaultModeLaunchArgument = "--mode=toggle-default";
    private ISynchronizationManager? _synchronizationManager;
    private IPreferencesService? _preferencesService;
    private IAccountService? _accountService;
    private bool _windowManagerConfigured;
    private bool _hasConfiguredAccounts;
    private bool _isExiting;
    private bool _activationInfrastructureInitialized;
    private bool _appHostInfrastructureInitialized;
    private bool _appNotificationsRegistered;
    private int _initialNotificationActivationHandled;
    private int _initialShareActivationHandled;
    private CancellationTokenSource? _autoSynchronizationLoopCts;
    private readonly SemaphoreSlim _autoSynchronizationSemaphore = new(1, 1);
    private readonly SemaphoreSlim _activationInfrastructureSemaphore = new(1, 1);
    private readonly SemaphoreSlim _appHostInfrastructureSemaphore = new(1, 1);
    private readonly ConcurrentDictionary<Guid, int> _inboxSyncCounters = [];
    private readonly AppNotificationActivationBuffer _bufferedAppNotificationActivations = new();
    private NativeTrayIcon? _trayIcon;
    private readonly record struct NotificationActivationRoute(bool RequiresForegroundWindow, Func<Task>? ExecuteAsync);
    private readonly record struct ShellWindowActivationResult(IWinoShellWindow? ShellWindow, bool WasCreated);

    internal bool IsExiting => _isExiting;

    public App()
    {
        InitializeComponent();

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        CryptographyContext.Register(typeof(WindowsSecureMimeContext));

        EnsureAppNotificationRegistration();
        RegisterRecipients();
    }

    private void EnsureWindowManagerConfigured()
    {
        if (_windowManagerConfigured)
            return;

        var windowManager = Services.GetRequiredService<IWinoWindowManager>();
        windowManager.ActiveWindowChanged -= OnActiveWindowChanged;
        windowManager.ActiveWindowChanged += OnActiveWindowChanged;
        windowManager.WindowRemoved -= OnManagedWindowRemoved;
        windowManager.WindowRemoved += OnManagedWindowRemoved;

        var nativeAppService = Services.GetRequiredService<INativeAppService>();
        nativeAppService.GetCoreWindowHwnd = () =>
        {
            var window = windowManager.ActiveWindow
                         ?? windowManager.GetWindow(WinoWindowKind.Shell)
                         ?? windowManager.GetWindow(WinoWindowKind.Welcome)
                         ?? MainWindow;

            return window == null
                ? IntPtr.Zero
                : WinRT.Interop.WindowNative.GetWindowHandle(window);
        };

        _windowManagerConfigured = true;
    }

    private void OnActiveWindowChanged(object? sender, WindowEx? window)
    {
        if (window == null)
            return;

        MainWindow = window;
        InitializeNavigationDispatcher();
    }

    private void OnManagedWindowRemoved(object? sender, WindowEx window)
    {
        var windowManager = Services.GetRequiredService<IWinoWindowManager>();
        MainWindow = windowManager.ActiveWindow
                     ?? windowManager.GetWindow(WinoWindowKind.Shell)
                     ?? windowManager.GetWindow(WinoWindowKind.Welcome);

        if (window is IWinoShellWindow)
        {
            DisposeTrayIcon();
        }

        InitializeNavigationDispatcher();
    }

    private void EnsureTrayIconCreated()
    {
        if (_trayIcon != null)
            return;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Wino_Icon.ico");
        var dispatcherQueue = DispatcherQueue.GetForCurrentThread()
                             ?? throw new InvalidOperationException("Tray icon must be created on a thread with a DispatcherQueue.");

        _trayIcon = new NativeTrayIcon(
            dispatcherQueue,
            iconPath,
            "Wino Mail",
            BuildTrayMenu,
            ActivatePreferredWindowAsync);

        _trayIcon.Create();
    }

    private void DisposeTrayIcon()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private void EnsurePreferenceChangedSubscription()
    {
        if (_preferencesService == null)
            return;

        _preferencesService.PreferenceChanged -= PreferencesServiceChanged;
        _preferencesService.PreferenceChanged += PreferencesServiceChanged;
    }

    private bool ShouldCreateTrayIcon()
        => _hasConfiguredAccounts &&
           HasShellWindow() &&
           (_preferencesService?.IsSystemTrayIconEnabled ?? true);

    private void UpdateTrayIconState(bool allowCreation)
    {
        if (!allowCreation || !ShouldCreateTrayIcon())
        {
            DisposeTrayIcon();
            return;
        }

        EnsureTrayIconCreated();
    }

    private IReadOnlyList<NativeTrayIcon.NativeTrayMenuItem> BuildTrayMenu()
    {
        List<NativeTrayIcon.NativeTrayMenuItem> items =
        [
            new(Translator.SystemTrayMenu_Open, ActivatePreferredWindowAsync, IsDefault: true),
            new(Translator.SystemTrayMenu_ShowWino, OpenMailFromTrayAsync)
        ];

        items.Add(new NativeTrayIcon.NativeTrayMenuItem(
            Translator.SystemTrayMenu_ShowWinoCalendar,
            OpenCalendarFromTrayAsync));
        items.Add(new NativeTrayIcon.NativeTrayMenuItem(
            Translator.SystemTrayMenu_ExitWino,
            ExitApplicationAsync));

        return items;
    }

    private Task ActivatePreferredWindowAsync()
    {
        if (!_hasConfiguredAccounts)
            return ActivateWelcomeWindowAsync();

        return LaunchEntryOrActivateShellAsync(_preferencesService?.DefaultApplicationMode ?? WinoApplicationMode.Mail);
    }

    private Task OpenMailFromTrayAsync()
        => _hasConfiguredAccounts
            ? LaunchEntryOrActivateShellAsync(WinoApplicationMode.Mail)
            : ActivateWelcomeWindowAsync();

    private Task OpenCalendarFromTrayAsync()
        => _hasConfiguredAccounts
            ? LaunchEntryOrActivateShellAsync(WinoApplicationMode.Calendar)
            : ActivateWelcomeWindowAsync();

    private async Task LaunchEntryOrActivateShellAsync(WinoApplicationMode mode)
    {
        if (AppEntryConstants.GetPackagedApplicationId(mode) != null)
        {
            var appEntryLauncher = Services.GetRequiredService<PackagedAppEntryLauncher>();
            if (await appEntryLauncher.LaunchAsync(mode))
                return;
        }

        await ActivateShellWindowAsync(mode);
    }

    private async Task ActivateWelcomeWindowAsync()
    {
        var windowManager = Services.GetRequiredService<IWinoWindowManager>();
        var welcomeWindow = windowManager.GetWindow(WinoWindowKind.Welcome) as WelcomeWindow;

        if (welcomeWindow == null)
        {
            CreateWelcomeWindow();
            welcomeWindow = MainWindow as WelcomeWindow;
        }

        if (welcomeWindow == null)
            return;

        CloseShellWindowIfPresent();
        await ActivateWindowAsync(welcomeWindow);
    }

    private async Task ActivateShellWindowAsync(WinoApplicationMode? mode, IWinoShellWindow? existingShellWindow = null)
    {
        var windowManager = Services.GetRequiredService<IWinoWindowManager>();
        var shellWindow = existingShellWindow;

        if (shellWindow == null)
        {
            shellWindow = windowManager.GetWindow(WinoWindowKind.Shell) as IWinoShellWindow;

            if (shellWindow == null)
            {
                CreateWindow(null);
                shellWindow = MainWindow as IWinoShellWindow;
            }
        }

        if (shellWindow == null)
            return;

        if (mode.HasValue)
            shellWindow.HandleAppActivation(AppEntryConstants.GetModeLaunchArgument(mode.Value));

        CloseWelcomeWindowIfPresent();
        await ActivateWindowAsync((WindowEx)shellWindow);
    }

    private void CloseWelcomeWindowIfPresent()
    {
        var windowManager = Services.GetRequiredService<IWinoWindowManager>();
        if (windowManager.GetWindow(WinoWindowKind.Welcome) is not WelcomeWindow welcomeWindow)
            return;

        welcomeWindow.PrepareForClose();
        welcomeWindow.AllowClose();
        welcomeWindow.Close();
    }

    private void CloseShellWindowIfPresent()
    {
        var windowManager = Services.GetRequiredService<IWinoWindowManager>();
        if (windowManager.GetWindow(WinoWindowKind.Shell) is not ShellWindow shellWindow)
            return;

        DisposeTrayIcon();
        windowManager.HideWindow(shellWindow);
        if (ReferenceEquals(MainWindow, shellWindow))
        {
            MainWindow = null;
            InitializeNavigationDispatcher();
        }

        shellWindow.PrepareForClose();
        shellWindow.Close();
    }

    private async Task ActivateWindowAsync(WindowEx window, bool applyThemeToWindow = true)
    {
        var windowManager = Services.GetRequiredService<IWinoWindowManager>();
        MainWindow = window;
        windowManager.ActivateWindow(window);

        if (applyThemeToWindow)
        {
            await NewThemeService.ApplyThemeToActiveWindowAsync();
        }

        UpdateTrayIconState(window is IWinoShellWindow);
    }

    private Task ExitApplicationAsync()
    {
        ExitApplication();
        return Task.CompletedTask;
    }

    private void ExitApplication()
    {
        if (_isExiting)
            return;

        _isExiting = true;
        DisposeTrayIcon();

        Services.GetRequiredService<IWinoWindowManager>().CloseAllWindows();
        Application.Current.Exit();
    }

    public bool IsNotificationActivation(out AppNotificationActivatedEventArgs args)
    {
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();

        if (activationArgs.Kind == ExtendedActivationKind.AppNotification)
        {
            args = ((AppNotificationActivatedEventArgs)activationArgs.Data);
            return true;
        }

        args = null!;
        return false;
    }

    #region Dependency Injection


    private void RegisterUWPServices(IServiceCollection services)
    {
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IMailDialogService, DialogService>();
        services.AddSingleton<IAiActionOptionsService, AiActionOptionsService>();
        services.AddSingleton<ReleaseLocalAccountDataCleanupService>();
        services.AddTransient<IProviderService, ProviderService>();
        services.AddSingleton<IAuthenticatorConfig, MailAuthenticatorConfiguration>();
        services.AddSingleton<IAccountCalendarStateService, AccountCalendarStateService>();
        services.AddSingleton<IDateContextProvider, SystemDateContextProvider>();
        services.AddSingleton<ICalendarRangeTextFormatter, CalendarRangeTextFormatter>();
    }

    private void RegisterViewModels(IServiceCollection services)
    {
        services.AddSingleton(typeof(MailAppShellViewModel));
        services.AddSingleton(typeof(CalendarAppShellViewModel));
        services.AddSingleton(typeof(ContactsShellClient));
        services.AddSingleton(typeof(SettingsShellClient));
        services.AddSingleton(typeof(WinoAppShellViewModel));
        services.AddSingleton<IMailShellClient>(serviceProvider => serviceProvider.GetRequiredService<MailAppShellViewModel>());
        services.AddSingleton<ICalendarShellClient>(serviceProvider => serviceProvider.GetRequiredService<CalendarAppShellViewModel>());
        services.AddSingleton<IShellClient>(serviceProvider => serviceProvider.GetRequiredService<MailAppShellViewModel>());
        services.AddSingleton<IShellClient>(serviceProvider => serviceProvider.GetRequiredService<CalendarAppShellViewModel>());
        services.AddSingleton<IShellClient>(serviceProvider => serviceProvider.GetRequiredService<ContactsShellClient>());
        services.AddSingleton<IShellClient>(serviceProvider => serviceProvider.GetRequiredService<SettingsShellClient>());

        services.AddTransient(typeof(MailListPageViewModel));
        services.AddTransient(typeof(MailRenderingPageViewModel));
        services.AddTransient(typeof(AccountManagementViewModel));
        services.AddTransient(typeof(WelcomePageV2ViewModel));
        services.AddTransient(typeof(ProviderSelectionPageViewModel));
        services.AddTransient(typeof(AccountSetupProgressPageViewModel));
        services.AddTransient(typeof(SpecialImapCredentialsPageViewModel));
        services.AddSingleton(typeof(WelcomeWizardContext));

        services.AddTransient(typeof(ComposePageViewModel));
        services.AddTransient(typeof(IdlePageViewModel));

        services.AddTransient(typeof(ImapCalDavSettingsPageViewModel));
        services.AddTransient(typeof(AccountDetailsPageViewModel));
        services.AddTransient(typeof(FolderCustomizationPageViewModel));
        services.AddTransient(typeof(SignatureManagementPageViewModel));
        services.AddTransient(typeof(MessageListPageViewModel));
        services.AddTransient(typeof(MailNotificationSettingsPageViewModel));
        services.AddTransient(typeof(ReadComposePanePageViewModel));
        services.AddTransient(typeof(MergedAccountDetailsPageViewModel));
        services.AddTransient(typeof(AppPreferencesPageViewModel));
        services.AddTransient(typeof(StoragePageViewModel));
        services.AddTransient(typeof(WinoAccountManagementPageViewModel));
        services.AddTransient(typeof(AliasManagementPageViewModel));
        services.AddTransient(typeof(MailCategoryManagementPageViewModel));
        services.AddTransient(typeof(ContactsPageViewModel));
        services.AddTransient(typeof(SignatureAndEncryptionPageViewModel));
        services.AddTransient(typeof(EmailTemplatesPageViewModel));
        services.AddTransient(typeof(CreateEmailTemplatePageViewModel));
        services.AddSingleton(typeof(CalendarPageViewModel));
        services.AddTransient(typeof(CalendarRenderingSettingsPageViewModel));
        services.AddTransient(typeof(CalendarNotificationSettingsPageViewModel));
        services.AddTransient(typeof(CalendarPreferenceSettingsPageViewModel));
        services.AddTransient(typeof(CalendarAccountSettingsPageViewModel));
        services.AddTransient(typeof(EventDetailsPageViewModel));
        services.AddTransient(typeof(CalendarEventComposePageViewModel));
    }

    #endregion

    public override IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.RegisterCoreServices();
        services.RegisterSharedServices();
        services.RegisterCoreUWPServices();
        services.RegisterCoreViewModels();

        RegisterUWPServices(services);
        RegisterViewModels(services);

        return services.BuildServiceProvider();
    }

    private bool IsStartupTaskLaunch() => AppInstance.GetCurrent().GetActivatedEventArgs()?.Kind == ExtendedActivationKind.StartupTask;
    public bool IsAppRunning()
    {
        var windowManager = Services.GetService<IWinoWindowManager>();

        return MainWindow != null ||
               windowManager?.GetWindow(WinoWindowKind.Shell) != null ||
               windowManager?.GetWindow(WinoWindowKind.Welcome) != null;
    }

    private bool HasShellWindow()
        => Services.GetRequiredService<IWinoWindowManager>().GetWindow(WinoWindowKind.Shell) is IWinoShellWindow;

    private async Task EnsureActivationInfrastructureAsync()
    {
        await EnsureCoreActivationInfrastructureAsync();
        await EnsureAppHostInfrastructureAsync();
    }

    private async Task EnsureCoreActivationInfrastructureAsync()
    {
        if (_activationInfrastructureInitialized)
            return;

        await _activationInfrastructureSemaphore.WaitAsync();

        try
        {
            if (_activationInfrastructureInitialized)
                return;

            EnsureAppNotificationRegistration();

            await Services.GetRequiredService<ReleaseLocalAccountDataCleanupService>()
                .RunIfNeededAsync();

            await InitializeServicesAsync();

            _synchronizationManager = Services.GetRequiredService<ISynchronizationManager>();
            _preferencesService = Services.GetRequiredService<IPreferencesService>();
            _accountService = Services.GetRequiredService<IAccountService>();

            _hasConfiguredAccounts = (await _accountService.GetAccountsAsync()).Any();

            _activationInfrastructureInitialized = true;
        }
        finally
        {
            _activationInfrastructureSemaphore.Release();
        }
    }

    private async Task EnsureAppHostInfrastructureAsync()
    {
        await EnsureCoreActivationInfrastructureAsync();

        if (_appHostInfrastructureInitialized)
            return;

        await _appHostInfrastructureSemaphore.WaitAsync();

        try
        {
            if (_appHostInfrastructureInitialized)
                return;

            EnsureWindowManagerConfigured();
            EnsurePreferenceChangedSubscription();

            if (_hasConfiguredAccounts)
            {
                RestartAutoSynchronizationLoop();
            }

            _appHostInfrastructureInitialized = true;
        }
        finally
        {
            _appHostInfrastructureSemaphore.Release();
        }
    }

    private bool TryMarkInitialNotificationActivationHandled()
        => Interlocked.Exchange(ref _initialNotificationActivationHandled, 1) == 0;

    private bool TryMarkInitialShareActivationHandled()
        => Interlocked.Exchange(ref _initialShareActivationHandled, 1) == 0;

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        await EnsureCoreActivationInfrastructureAsync();

        var activationArgs = ResolveStartupActivation();

        if (await TryHandleStartupAppNotificationActivationAsync(activationArgs))
            return;

        await EnsureAppHostInfrastructureAsync();

        var hasAnyAccount = _hasConfiguredAccounts;
        var isStartupTaskLaunch = activationArgs.Kind == ExtendedActivationKind.StartupTask;

        if (!hasAnyAccount && !isStartupTaskLaunch)
        {
            await LaunchWelcomeWindowAsync();
            return;
        }

        if (await TryHandleLaunchActivationAsync(args, activationArgs))
            return;

        await CompleteStandardLaunchAsync(args, hasAnyAccount, isStartupTaskLaunch);
    }

    private AppActivationArguments ResolveStartupActivation()
    {
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (Program.TryConsumeDeferredAppNotificationStartup())
        {
            LogActivation($"Resolved deferred COM activation after notification registration. Kind: {activationArgs.Kind}");
        }

        return activationArgs;
    }

    private async Task<bool> TryHandleStartupAppNotificationActivationAsync(AppActivationArguments activationArgs)
    {
        if (activationArgs.Kind != ExtendedActivationKind.AppNotification ||
            activationArgs.Data is not AppNotificationActivatedEventArgs toastArgs ||
            !TryMarkInitialNotificationActivationHandled())
        {
            return false;
        }

        LogActivation($"Processing app-notification activation from startup. Arguments: {toastArgs.Argument}");

        if (!TryResolveNotificationActivationRoute(toastArgs, out var route))
        {
            await HandleToastActivationAsync(toastArgs.Argument, toastArgs.UserInput);

            if (!IsAppRunning())
            {
                LogActivation("Startup app-notification activation completed without a window. Exiting transient process.");
                ExitApplication();
            }

            return true;
        }

        if (route.RequiresForegroundWindow)
        {
            await EnsureAppHostInfrastructureAsync();
            await route.ExecuteAsync!.Invoke();
            return true;
        }

        await route.ExecuteAsync!.Invoke();

        if (!IsAppRunning())
        {
            LogActivation("Background startup app-notification activation completed. Exiting without creating app host.");
            ExitApplication();
        }

        return true;
    }

    private void AppNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (!_activationInfrastructureInitialized)
        {
            LogActivation($"Buffering app notification activation until infrastructure is ready. Arguments: {args.Argument}");
            _bufferedAppNotificationActivations.Enqueue(args);
            return;
        }

        // AppNotification callbacks are not guaranteed to run on the UI thread.
        // Marshal toast handling to the window dispatcher before touching window APIs.
        if (TryEnqueueActivationOnUiThread(() => _ = HandleToastActivationAsync(args.Argument, args.UserInput)))
            return;

        LogActivation($"Processing notification activation from NotificationInvoked. Arguments: {args.Argument}");
        _ = HandleToastActivationAsync(args.Argument, args.UserInput);
    }

    private bool TryResolveNotificationActivationRoute(AppNotificationActivatedEventArgs notificationArgs,
                                                       out NotificationActivationRoute route)
    {
        route = default;

        if (!ToastActivationResolver.TryParse(notificationArgs.Argument, out var toastArguments))
            return false;

        return TryCreateNotificationActivationRoute(toastArguments, notificationArgs.UserInput, out route);
    }

    private void EnsureAppNotificationRegistration()
    {
        if (!Program.ShouldRegisterAppNotifications())
        {
            LogActivation("Skipping app notification registration for non-host entry activation.");
            return;
        }

        if (_appNotificationsRegistered)
            return;

        var notificationManager = AppNotificationManager.Default;

        notificationManager.NotificationInvoked -= AppNotificationInvoked;
        notificationManager.NotificationInvoked += AppNotificationInvoked;

        try
        {
            notificationManager.Register();
            _appNotificationsRegistered = true;
        }
        catch (Exception ex)
        {
            LogActivation($"App notification registration failed: {ex.GetType().Name} - {ex.Message}");
        }
    }

    /// <summary>
    /// Handles toast notification activation scenarios.
    /// </summary>
    private async Task HandleToastActivationAsync(NotificationArguments toastArguments, IDictionary<string, string>? userInput = null)
    {
        if (!TryCreateNotificationActivationRoute(toastArguments, userInput, out var route))
        {
            LogActivation("App notification activation did not match any known handler.");
            return;
        }

        LogActivation(route.RequiresForegroundWindow
            ? "Handling foreground app notification activation."
            : "Handling background app notification activation.");

        await route.ExecuteAsync!.Invoke();
    }

    private Task HandleToastActivationAsync(string toastArgument, IDictionary<string, string>? userInput = null)
    {
        if (!ToastActivationResolver.TryParse(toastArgument, out var toastArguments))
        {
            LogActivation($"Ignoring non-toast launch argument: {toastArgument}");
            return Task.CompletedTask;
        }

        return HandleToastActivationAsync(toastArguments, userInput);
    }

    private async Task<bool> HandleShareTargetActivationAsync(AppActivationArguments activationArgs, bool activateWindow)
    {
        if (activationArgs.Kind != ExtendedActivationKind.ShareTarget ||
            activationArgs.Data is not ShareTargetActivatedEventArgs shareTargetArgs)
        {
            return false;
        }

        var shareRequest = await ExtractMailShareRequestAsync(shareTargetArgs);

        if (shareRequest?.Files == null || shareRequest.Files.Count == 0)
        {
            Services.GetRequiredService<IShareActivationService>().ClearPendingShareRequest();
            return false;
        }

        var shareActivationService = Services.GetRequiredService<IShareActivationService>();
        shareActivationService.PendingShareRequest = shareRequest;

        if (!_hasConfiguredAccounts)
        {
            shareActivationService.ClearPendingShareRequest();
            return false;
        }

        var shellWindowAlreadyExists = HasShellWindow();

        await EnsureShellWindowAsync(WinoApplicationMode.Mail, activateWindow, suppressStartupFlows: true);

        if (shellWindowAlreadyExists)
        {
            await Services.GetRequiredService<MailAppShellViewModel>().HandlePendingShareRequestAsync();
        }

        return true;
    }

    private async Task<bool> TryHandleLaunchActivationAsync(Microsoft.UI.Xaml.LaunchActivatedEventArgs args,
                                                            AppActivationArguments activationArgs)
    {
        if (activationArgs.Kind == ExtendedActivationKind.ShareTarget &&
            TryMarkInitialShareActivationHandled())
        {
            LogActivation("Processing share target activation from OnLaunched.");

            if (await HandleShareTargetActivationAsync(activationArgs, activateWindow: true))
                return true;
        }

        if (Program.TryConsumePendingBootstrapActivation(out var pendingBootstrapActivation))
        {
            LogActivation($"Processing pending bootstrap activation. Kind: {pendingBootstrapActivation.Kind}, Mode: {pendingBootstrapActivation.Mode}");

            if (await HandlePendingBootstrapActivationAsync(pendingBootstrapActivation))
                return true;
        }

        if (ToastActivationResolver.TryParse(args.Arguments, out var launchToastArguments) &&
            TryMarkInitialNotificationActivationHandled())
        {
            LogActivation($"Processing toast launch activation from OnLaunched. Arguments: {args.Arguments}");
            await HandleToastActivationAsync(launchToastArguments);
            return true;
        }

        return false;
    }

    private async Task<MailShareRequest?> ExtractMailShareRequestAsync(ShareTargetActivatedEventArgs shareTargetArgs)
    {
        var shareOperation = shareTargetArgs.ShareOperation;

        try
        {
            shareOperation.ReportStarted();

            if (!shareOperation.Data.Contains(StandardDataFormats.StorageItems))
            {
                shareOperation.ReportCompleted();
                return null;
            }

            var storageItems = await shareOperation.Data.GetStorageItemsAsync();
            List<SharedFile> sharedFiles = [];

            foreach (var storageFile in storageItems.OfType<StorageFile>())
            {
                sharedFiles.Add(await storageFile.ToSharedFileAsync());
            }

            shareOperation.ReportDataRetrieved();
            shareOperation.ReportCompleted();

            return sharedFiles.Count == 0
                ? null
                : new MailShareRequest(sharedFiles);
        }
        catch (Exception ex)
        {
            LogActivation($"Failed to extract share target payload: {ex.GetType().Name} - {ex.Message}");

            try
            {
                shareOperation.ReportError(ex.Message);
            }
            catch
            {
                // Ignore share reporting failures and fall back to normal launch flow.
            }

            return null;
        }
    }

    private async Task LaunchWelcomeWindowAsync()
    {
        CreateWelcomeWindow();
        await NewThemeService.InitializeAsync();
        MainWindow?.Activate();
        LogActivation("Welcome window created and activated.");
    }

    private async Task<ShellWindowActivationResult> EnsureShellWindowAsync(WinoApplicationMode mode,
                                                                           bool activateWindow,
                                                                           bool suppressStartupFlows = true,
                                                                           object? activationParameter = null)
    {
        var windowManager = Services.GetRequiredService<IWinoWindowManager>();
        var navigationService = Services.GetRequiredService<INavigationService>();
        var shellWindow = windowManager.GetWindow(WinoWindowKind.Shell) as IWinoShellWindow;
        var wasCreated = false;

        if (shellWindow == null)
        {
            LogActivation($"Creating shell window for {mode} activation.");
            wasCreated = true;

            CreateWindow(
                null,
                AppEntryConstants.GetModeLaunchArgument(mode),
                new ShellModeActivationContext
                {
                    SuppressStartupFlows = suppressStartupFlows,
                    Parameter = activationParameter
                });

            await NewThemeService.InitializeAsync();

            if (_hasConfiguredAccounts)
            {
                await LoadInitialWinoAccountAsync();
            }

            shellWindow = windowManager.GetWindow(WinoWindowKind.Shell) as IWinoShellWindow ?? MainWindow as IWinoShellWindow;
        }
        else
        {
            ApplyShellWindowTaskbarIdentity(shellWindow, mode);
            navigationService.ChangeApplicationMode(mode, new ShellModeActivationContext
            {
                SuppressStartupFlows = suppressStartupFlows,
                Parameter = activationParameter
            });
        }

        ApplyShellWindowTaskbarIdentity(shellWindow, mode);

        if (activateWindow && shellWindow is WindowEx window)
        {
            await ActivateWindowAsync(window, applyThemeToWindow: wasCreated);
        }

        return new ShellWindowActivationResult(shellWindow, wasCreated);
    }

    private async Task HandleStoreUpdateToastAsync()
    {
        if (!IsAppRunning())
            await CreateAndActivateWindow(null!);
        else
            EnsureMainWindowVisibleAndForeground();

        var storeUpdateService = Services.GetRequiredService<IStoreUpdateService>();
        await storeUpdateService.StartUpdateAsync();
    }

    private async Task HandleCalendarToastNavigationAsync(Guid calendarItemId)
    {
        var calendarService = Services.GetRequiredService<ICalendarService>();
        var fallbackNavigationArgs = new CalendarPageNavigationArgs
        {
            RequestDefaultNavigation = true
        };

        if (!HasShellWindow())
        {
            await EnsureShellWindowAsync(
                WinoApplicationMode.Calendar,
                activateWindow: true,
                activationParameter: fallbackNavigationArgs);
        }

        var calendarItem = await calendarService.GetCalendarItemAsync(calendarItemId);
        if (calendarItem == null)
        {
            LogActivation($"Calendar notification navigation item was not found for {calendarItemId}. Opening calendar shell only.");

            await EnsureShellWindowAsync(
                WinoApplicationMode.Calendar,
                activateWindow: true,
                activationParameter: fallbackNavigationArgs);
            return;
        }

        var target = new CalendarItemTarget(calendarItem, CalendarEventTargetType.Single);
        var navigationArgs = new CalendarPageNavigationArgs
        {
            NavigationDate = calendarItem.LocalStartDate,
            PendingTarget = target
        };

        await EnsureShellWindowAsync(
            WinoApplicationMode.Calendar,
            activateWindow: true,
            activationParameter: navigationArgs);
    }

    private async Task HandleCalendarToastSnoozeAsync(IDictionary<string, string>? userInput, Guid calendarItemId)
    {
        if (!TryGetSnoozeDurationMinutes(userInput, out var snoozeDurationMinutes))
            return;

        var calendarService = Services.GetRequiredService<ICalendarService>();
        var snoozedUntilLocal = DateTime.Now.AddMinutes(snoozeDurationMinutes);

        await calendarService.SnoozeCalendarItemAsync(calendarItemId, snoozedUntilLocal);
    }

    private async Task HandleCalendarToastJoinOnlineAsync(Guid calendarItemId)
    {
        var calendarService = Services.GetRequiredService<ICalendarService>();
        var nativeAppService = Services.GetRequiredService<INativeAppService>();

        var calendarItem = await calendarService.GetCalendarItemAsync(calendarItemId);
        if (calendarItem == null ||
            !Uri.TryCreate(calendarItem.HtmlLink, UriKind.Absolute, out var joinUri))
        {
            return;
        }

        await nativeAppService.LaunchUriAsync(joinUri);
    }

    private async Task CompleteStandardLaunchAsync(Microsoft.UI.Xaml.LaunchActivatedEventArgs args,
                                                   bool hasAnyAccount,
                                                   bool isStartupTaskLaunch)
    {
        if (isStartupTaskLaunch && !hasAnyAccount)
        {
            CreateWelcomeWindow();
        }
        else
        {
            CreateWindow(args);
        }

        await NewThemeService.InitializeAsync();

        if (hasAnyAccount)
        {
            await LoadInitialWinoAccountAsync();
        }

        LogActivation("Theme service initialized.");

        if (isStartupTaskLaunch)
        {
            UpdateTrayIconState(allowCreation: true);
            LogActivation("Launched by startup task. Window created but hidden (system tray only).");
            return;
        }

        if (MainWindow is WindowEx window)
        {
            await ActivateWindowAsync(window, applyThemeToWindow: false);
        }

        LogActivation("Window created and activated.");
    }

    private bool TryGetSnoozeDurationMinutes(IDictionary<string, string>? userInput, out int snoozeDurationMinutes)
    {
        snoozeDurationMinutes = _preferencesService?.DefaultSnoozeDurationInMinutes ?? 0;

        if (userInput == null ||
            !userInput.TryGetValue(Constants.ToastCalendarSnoozeDurationInputId, out var selectedValue) ||
            selectedValue == null)
        {
            return snoozeDurationMinutes > 0;
        }

        var selectedText = selectedValue.ToString();

        return int.TryParse(selectedText, out snoozeDurationMinutes) && snoozeDurationMinutes > 0;
    }

    /// <summary>
    /// Handles toast notification click for navigation.
    /// Creates window if not running, sets up navigation parameter.
    /// </summary>
    private async Task HandleToastNavigationAsync(Guid mailItemUniqueId)
    {
        var mailService = Services.GetRequiredService<IMailService>();

        var account = await mailService.GetMailAccountByUniqueIdAsync(mailItemUniqueId);
        if (account == null)
        {
            LogActivation($"Notification navigation mail account was not found for {mailItemUniqueId}.");
            return;
        }

        var mailItem = await mailService.GetSingleMailItemAsync(mailItemUniqueId);
        if (mailItem == null)
        {
            LogActivation($"Notification navigation mail item was not found for {mailItemUniqueId}.");
            return;
        }

        var message = new AccountMenuItemExtended(mailItem.AssignedFolder.Id, mailItem);

        // Store navigation parameter in LaunchProtocolService so AppShell can pick it up.
        var launchProtocolService = Services.GetRequiredService<ILaunchProtocolService>();
        launchProtocolService.LaunchParameter = message;

        var shellWindowAlreadyExists = HasShellWindow();

        await EnsureShellWindowAsync(WinoApplicationMode.Mail, activateWindow: true);

        if (shellWindowAlreadyExists)
        {
            WeakReferenceMessenger.Default.Send(message);
            launchProtocolService.LaunchParameter = null;
        }
    }

    /// <summary>
    /// Handles toast action button clicks (Mark as Read, Delete, etc.).
    /// Executes the action without showing UI and exits the app.
    /// </summary>
    private async Task HandleToastActionAsync(MailOperation action, Guid mailItemUniqueId)
    {
        LogActivation($"Handling toast action: {action} for mail {mailItemUniqueId}");

        var mailService = Services.GetRequiredService<IMailService>();
        var mailItem = await mailService.GetSingleMailItemAsync(mailItemUniqueId);

        if (mailItem == null)
        {
            LogActivation("Mail item not found. Exiting.");
            ExitApplication();
            return;
        }

        var package = new MailOperationPreperationRequest(action, mailItem);

        // Check if app is already running (has a window).
        if (HasShellWindow())
        {
            // App is running - use the simple delegator pattern.
            // The synchronization will happen in the background.
            LogActivation("App is running. Queueing request via delegator.");

            var delegator = Services.GetRequiredService<IWinoRequestDelegator>();
            await delegator.ExecuteAsync(package);

            // Don't exit - app continues running.
            LogActivation($"Toast action {action} queued successfully.");
        }
        else
        {
            // App is not running - we need to wait for sync before exiting.
            LogActivation("App is not running. Executing synchronization and waiting for completion.");

            if (_synchronizationManager == null)
            {
                LogActivation("Synchronization manager is not initialized. Exiting.");
                ExitApplication();
                return;
            }

            var processor = Services.GetRequiredService<IWinoRequestProcessor>();
            var notificationBuilder = Services.GetRequiredService<INotificationBuilder>();

            // Prepare the requests for the action.
            var requests = await processor.PrepareRequestsAsync(package);

            if (requests != null && requests.Any())
            {
                // Group requests by account ID (usually just one account).
                var accountIds = requests.GroupBy(a => a.Item.AssignedAccount.Id);

                foreach (var accountGroup in accountIds)
                {
                    var accountId = accountGroup.Key;

                    // Queue all requests for this account.
                    foreach (var request in accountGroup)
                    {
                        await _synchronizationManager.QueueRequestAsync(request, accountId, triggerSynchronization: false);
                    }

                    // Create synchronization options to execute the queued requests.
                    var syncOptions = new MailSynchronizationOptions()
                    {
                        AccountId = accountId,
                        Type = MailSynchronizationType.ExecuteRequests
                    };

                    LogActivation($"Executing synchronization for account {accountId}...");

                    // Wait for synchronization to complete before exiting.
                    var syncResult = await _synchronizationManager.SynchronizeMailAsync(syncOptions);

                    LogActivation($"Toast action {action} completed. Sync result: {syncResult.CompletedState}");
                }

                await notificationBuilder.UpdateTaskbarIconBadgeAsync();
            }

            LogActivation("Toast action handling complete. Exiting app.");

            // Exit the app after synchronization is complete.
            ExitApplication();
        }
    }

    private async Task HandleToastComposeActionAsync(MailOperation action, Guid mailItemUniqueId)
    {
        LogActivation($"Handling compose toast action: {action} for mail {mailItemUniqueId}");

        var mailService = Services.GetRequiredService<IMailService>();
        var folderService = Services.GetRequiredService<IFolderService>();
        var mimeFileService = Services.GetRequiredService<IMimeFileService>();
        var navigationService = Services.GetRequiredService<INavigationService>();
        var requestDelegator = Services.GetRequiredService<IWinoRequestDelegator>();
        var mailShellViewModel = Services.GetRequiredService<MailAppShellViewModel>();

        var mailItem = await mailService.GetSingleMailItemAsync(mailItemUniqueId);
        if (mailItem == null)
        {
            LogActivation($"Compose toast mail item was not found for {mailItemUniqueId}.");
            return;
        }

        var account = await mailService.GetMailAccountByUniqueIdAsync(mailItemUniqueId) ?? mailItem.AssignedAccount;
        if (account == null)
        {
            LogActivation($"Compose toast account was not found for {mailItemUniqueId}.");
            return;
        }

        var draftFolder = await folderService.GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Draft);
        if (draftFolder == null)
        {
            LogActivation($"Compose toast draft folder is missing for account {account.Id}.");
            return;
        }

        var mimeInformation = await mimeFileService.GetMimeMessageInformationAsync(mailItem.FileId, account.Id);
        if (mimeInformation?.MimeMessage == null)
        {
            LogActivation($"Compose toast MIME payload was not found for mail {mailItemUniqueId}.");
            return;
        }

        await EnsureShellWindowAsync(WinoApplicationMode.Mail, activateWindow: true);

        if (mailShellViewModel.MenuItems.TryGetAccountMenuItem(account.Id, out IAccountMenuItem accountMenuItem))
        {
            await mailShellViewModel.ChangeLoadedAccountAsync(accountMenuItem, navigateInbox: false);
        }

        if (mailShellViewModel.MenuItems.TryGetSpecialFolderMenuItem(account.Id, SpecialFolderType.Draft, out var draftFolderMenuItem))
        {
            await mailShellViewModel.NavigateFolderAsync(draftFolderMenuItem);
        }

        var draftOptions = new DraftCreationOptions
        {
            Reason = action switch
            {
                MailOperation.Reply => DraftCreationReason.Reply,
                MailOperation.ReplyAll => DraftCreationReason.ReplyAll,
                MailOperation.Forward => DraftCreationReason.Forward,
                _ => DraftCreationReason.Empty
            },
            ReferencedMessage = new ReferencedMessage
            {
                MimeMessage = mimeInformation.MimeMessage,
                MailCopy = mailItem
            }
        };

        var (draftMailCopy, draftBase64MimeMessage) = await mailService.CreateDraftAsync(account.Id, draftOptions);
        var draftPreparationRequest = new DraftPreparationRequest(account, draftMailCopy, draftBase64MimeMessage, draftOptions.Reason, mailItem);

        await requestDelegator.ExecuteAsync(draftPreparationRequest);
        navigationService.Navigate(WinoPage.ComposePage,
                                   new MailItemViewModel(draftMailCopy),
                                   NavigationReferenceFrame.RenderingFrame,
                                   NavigationTransitionType.DrillIn);
    }

    private static bool IsComposeToastAction(MailOperation action)
        => action is MailOperation.Reply or MailOperation.ReplyAll or MailOperation.Forward;

    /// <summary>
    /// Creates the main window and activates it.
    /// </summary>
    private async Task CreateAndActivateWindow(Microsoft.UI.Xaml.LaunchActivatedEventArgs? args)
    {
        CreateWindow(args);

        // Initialize theme service after window is created.
        await NewThemeService.InitializeAsync();

        if (MainWindow is WindowEx window)
        {
            await ActivateWindowAsync(window, applyThemeToWindow: false);
        }

        LogActivation("Window created and activated.");
    }

    public Task OpenManageAccountsFromWelcomeAsync()
    {
        Services.GetRequiredService<INavigationService>()
            .Navigate(WinoPage.SettingsPage, WinoPage.ManageAccountsPage, NavigationReferenceFrame.ShellFrame, NavigationTransitionType.DrillIn);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates the main window without activating it.
    /// Used for both normal launch and startup task launch (tray only).
    /// </summary>
    private void CreateWindow(Microsoft.UI.Xaml.LaunchActivatedEventArgs? args,
                              string? forcedLaunchArguments = null,
                              ShellModeActivationContext? activationContextOverride = null)
    {
        LogActivation("Creating main window.");

        var windowManager = Services.GetRequiredService<IWinoWindowManager>();
        MainWindow = windowManager.CreateWindow(WinoWindowKind.Shell, () => new ShellWindow());
        InitializeNavigationDispatcher();

        if (MainWindow is not IWinoShellWindow shellWindow)
            throw new ArgumentException("MainWindow must implement IWinoShellWindow");

        windowManager.SetPrimaryNavigationFrame(WinoWindowKind.Shell, shellWindow.GetMainFrame());

        var navigationService = Services.GetRequiredService<INavigationService>();
        var defaultMode = _preferencesService?.DefaultApplicationMode ?? WinoApplicationMode.Mail;
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();

        if (activationContextOverride != null)
        {
            var targetMode = !string.IsNullOrWhiteSpace(forcedLaunchArguments)
                ? AppModeActivationResolver.Resolve(forcedLaunchArguments, null, null, defaultMode)
                : TryResolveActivationMode(activationArgs, defaultMode, out var resolvedActivationMode)
                    ? resolvedActivationMode
                    : AppModeActivationResolver.Resolve(args?.Arguments, GetCurrentLaunchTileId(), Environment.CommandLine, defaultMode);

            ApplyShellWindowTaskbarIdentity(shellWindow, targetMode);
            navigationService.ChangeApplicationMode(targetMode, activationContextOverride);
            return;
        }

        if (!string.IsNullOrWhiteSpace(forcedLaunchArguments))
        {
            shellWindow.HandleAppActivation(forcedLaunchArguments);
            return;
        }

        if (activationArgs.Kind == ExtendedActivationKind.Launch &&
            activationArgs.Data is ILaunchActivatedEventArgs launchArgs)
        {
            var launchArguments = launchArgs.Arguments;

            if (Program.TryConsumeCurrentProcessAlternateModeOverride())
            {
                launchArguments = AppendLaunchArgument(launchArguments, ToggleDefaultModeLaunchArgument);
            }

            shellWindow.HandleAppActivation(launchArguments, launchArgs.TileId, Environment.CommandLine);
            return;
        }

        if (TryResolveActivationMode(activationArgs, defaultMode, out var activationMode))
        {
            shellWindow.HandleAppActivation(AppEntryConstants.GetModeLaunchArgument(activationMode));
            return;
        }

        shellWindow.HandleAppActivation(args?.Arguments, GetCurrentLaunchTileId(), Environment.CommandLine);
    }

    private static void ApplyShellWindowTaskbarIdentity(IWinoShellWindow? shellWindow, WinoApplicationMode mode)
    {
        if (shellWindow is not WindowEx window)
            return;

        var packagedApplicationId = AppEntryConstants.GetPackagedApplicationId(mode);
        if (packagedApplicationId == null)
            return;

        WindowAppUserModelIdHelper.TrySet(window, AppEntryConstants.GetAppUserModelId(mode));
    }

    private void CreateWelcomeWindow()
    {
        LogActivation("Creating welcome window.");

        var windowManager = Services.GetRequiredService<IWinoWindowManager>();
        MainWindow = windowManager.CreateWindow(WinoWindowKind.Welcome, () => new WelcomeWindow());
        if (MainWindow is WelcomeWindow welcomeWindow)
        {
            var rootFrame = welcomeWindow.GetRootFrame();
            windowManager.SetPrimaryNavigationFrame(WinoWindowKind.Welcome, rootFrame);

            if (rootFrame.Content is WelcomeHostPage welcomeHostPage)
            {
                welcomeHostPage.ResetWizard();
            }
            else
            {
                rootFrame.BackStack.Clear();
                rootFrame.ForwardStack.Clear();
                rootFrame.Navigate(typeof(WelcomeHostPage), null, new SuppressNavigationTransitionInfo());
            }
        }

        InitializeNavigationDispatcher();
    }

    private void InitializeNavigationDispatcher()
    {
        if (MainWindow == null)
            return;

        if (Services.GetService<IDispatcher>() is WinUIDispatcher dispatcher)
        {
            dispatcher.Initialize(MainWindow.DispatcherQueue);
        }
    }

    private void EnsureMainWindowVisibleAndForeground()
    {
        var windowManager = Services.GetRequiredService<IWinoWindowManager>();
        var currentWindow = windowManager.ActiveWindow
                            ?? windowManager.GetWindow(WinoWindowKind.Shell)
                            ?? windowManager.GetWindow(WinoWindowKind.Welcome)
                            ?? MainWindow;

        if (currentWindow == null)
            return;

        MainWindow = currentWindow;
        windowManager.ActivateWindow(currentWindow);
    }

    private void RegisterRecipients()
    {
        WeakReferenceMessenger.Default.Register<NewMailSynchronizationRequested>(this);
        WeakReferenceMessenger.Default.Register<NewCalendarSynchronizationRequested>(this);
        WeakReferenceMessenger.Default.Register<AccountCreatedMessage>(this);
        WeakReferenceMessenger.Default.Register<AccountRemovedMessage>(this);
        WeakReferenceMessenger.Default.Register<GetStartedFromWelcomeRequested>(this);
        WeakReferenceMessenger.Default.Register<WelcomeImportCompletedMessage>(this);
    }

    public async void Receive(NewMailSynchronizationRequested message)
    {
        if (_synchronizationManager == null) return;

        MailSynchronizationResult syncResult;

        try
        {
            syncResult = await _synchronizationManager.SynchronizeMailAsync(message.Options);
        }
        catch (Exception ex)
        {
            // Defensive fallback to guarantee completion message emission.
            syncResult = MailSynchronizationResult.Failed(ex);
        }

        WeakReferenceMessenger.Default.Send(new AccountSynchronizationCompleted(
            message.Options.AccountId,
            syncResult.CompletedState,
            message.Options.GroupedSynchronizationTrackingId,
            message.Options.Type));

        if (syncResult.CompletedState is SynchronizationCompletedState.Success or SynchronizationCompletedState.PartiallyCompleted)
        {
            await ClearInvalidCredentialAttentionIfNeededAsync(message.Options.AccountId);
        }

        if (syncResult.CompletedState == SynchronizationCompletedState.Failed ||
            syncResult.CompletedState == SynchronizationCompletedState.PartiallyCompleted)
        {
            var dialogService = Services.GetRequiredService<IMailDialogService>();
            var errorMessage = GetSynchronizationFailureMessage(message.Options.Type, syncResult.AllIssues, syncResult.Exception?.Message);
            var severity = syncResult.CompletedState == SynchronizationCompletedState.PartiallyCompleted
                ? InfoBarMessageType.Warning
                : InfoBarMessageType.Error;

            dialogService.InfoBarMessage(Translator.Info_SyncFailedTitle, errorMessage, severity);
        }
    }

    public async void Receive(NewCalendarSynchronizationRequested message)
    {
        if (_synchronizationManager == null) return;

        var calendarSyncResult = await _synchronizationManager.SynchronizeCalendarAsync(message.Options);

        if (calendarSyncResult.CompletedState is SynchronizationCompletedState.Failed or SynchronizationCompletedState.PartiallyCompleted)
        {
            var dialogService = Services.GetRequiredService<IMailDialogService>();
            dialogService.InfoBarMessage(
                Translator.Info_SyncFailedTitle,
                GetCalendarSynchronizationFailureMessage(message.Options.Type, calendarSyncResult.AllIssues, calendarSyncResult.Exception?.Message),
                calendarSyncResult.CompletedState == SynchronizationCompletedState.PartiallyCompleted
                    ? InfoBarMessageType.Warning
                    : InfoBarMessageType.Error);
        }
    }

    public void Receive(AccountCreatedMessage message)
    {
        _hasConfiguredAccounts = true;
        EnsurePreferenceChangedSubscription();

        var windowManager = Services.GetRequiredService<IWinoWindowManager>();

        // Only transition when the account was created from the WelcomeWindow.
        if (windowManager.GetWindow(WinoWindowKind.Welcome) == null)
            return;

        MainWindow?.DispatcherQueue?.TryEnqueue(async () =>
        {
            // Create and activate ShellWindow — ActiveWindowChanged fires and rebinds the dispatcher.
            CreateWindow(null, AppEntryConstants.GetModeLaunchArgument(WinoApplicationMode.Mail));
            CloseWelcomeWindowIfPresent();
            if (MainWindow != null)
                await ActivateWindowAsync(MainWindow);

            if (message.Account.IsCalendarAccessGranted)
            {
                WeakReferenceMessenger.Default.Send(new NewCalendarSynchronizationRequested(new CalendarSynchronizationOptions
                {
                    AccountId = message.Account.Id,
                    Type = CalendarSynchronizationType.CalendarEvents
                }));
            }

            RestartAutoSynchronizationLoop();
        });
    }

    public void Receive(WelcomeImportCompletedMessage message)
    {
        _hasConfiguredAccounts = message.ImportedMailboxCount > 0;

        var windowManager = Services.GetRequiredService<IWinoWindowManager>();
        if (windowManager.GetWindow(WinoWindowKind.Welcome) == null)
            return;

        MainWindow?.DispatcherQueue?.TryEnqueue(async () =>
        {
            EnsurePreferenceChangedSubscription();

            CreateWindow(
                null,
                AppEntryConstants.GetModeLaunchArgument(WinoApplicationMode.Mail),
                new ShellModeActivationContext
                {
                    SuppressStartupFlows = true
                });

            await LoadInitialWinoAccountAsync();
            CloseWelcomeWindowIfPresent();

            if (MainWindow != null)
            {
                await ActivateWindowAsync(MainWindow);
            }

            RestartAutoSynchronizationLoop();

            Services.GetRequiredService<IMailDialogService>().InfoBarMessage(
                Translator.GeneralTitle_Info,
                Translator.WinoAccount_Management_ImportReloginReminder,
                InfoBarMessageType.Information);
        });
    }

    public void Receive(AccountRemovedMessage message)
    {
        var windowManager = Services.GetRequiredService<IWinoWindowManager>();

        // Only handle when ShellWindow is active (not during wizard rollback)
        if (windowManager.GetWindow(WinoWindowKind.Shell) == null)
            return;

        MainWindow?.DispatcherQueue?.TryEnqueue(async () =>
        {
            var accounts = await _accountService!.GetAccountsAsync();
            _hasConfiguredAccounts = accounts.Any();
            if (_hasConfiguredAccounts) return;

            // All accounts removed — go back to welcome wizard from step 1
            Services.GetRequiredService<WelcomeWizardContext>().Reset();
            StopAutoSynchronizationLoop();
            UpdateTrayIconState(allowCreation: false);
            CloseShellWindowIfPresent();
            CreateWelcomeWindow();
            if (MainWindow != null)
                await ActivateWindowAsync(MainWindow);
        });
    }

    public void Receive(GetStartedFromWelcomeRequested message)
    {
        var windowManager = Services.GetRequiredService<IWinoWindowManager>();

        if (windowManager.GetWindow(WinoWindowKind.Welcome) == null)
            return;

        MainWindow?.DispatcherQueue?.TryEnqueue(async () =>
        {
            CreateWindow(null);
            windowManager.HideWindow(WinoWindowKind.Welcome);
            await NewThemeService.ApplyThemeToActiveWindowAsync();
            MainWindow?.Activate();
        });
    }

    private static string GetSynchronizationFailureMessage(
        MailSynchronizationType synchronizationType,
        IEnumerable<SynchronizationIssue> issues,
        string? exceptionMessage)
    {
        var issueMessage = FormatSynchronizationIssues(issues);
        if (!string.IsNullOrWhiteSpace(issueMessage))
        {
            return issueMessage;
        }

        if (!string.IsNullOrWhiteSpace(exceptionMessage))
        {
            return exceptionMessage;
        }

        return synchronizationType switch
        {
            MailSynchronizationType.Alias => Translator.Exception_FailedToSynchronizeAliases,
            MailSynchronizationType.Categories => Translator.Exception_FailedToSynchronizeCategories,
            MailSynchronizationType.UpdateProfile => Translator.Exception_FailedToSynchronizeProfileInformation,
            _ => Translator.Exception_FailedToSynchronizeFolders
        };
    }

    private static string GetCalendarSynchronizationFailureMessage(
        CalendarSynchronizationType synchronizationType,
        IEnumerable<SynchronizationIssue> issues,
        string? exceptionMessage)
    {
        var issueMessage = FormatSynchronizationIssues(issues);
        if (!string.IsNullOrWhiteSpace(issueMessage))
        {
            return issueMessage;
        }

        if (!string.IsNullOrWhiteSpace(exceptionMessage))
        {
            return exceptionMessage;
        }

        return synchronizationType switch
        {
            CalendarSynchronizationType.CalendarMetadata => Translator.Exception_FailedToSynchronizeCalendarMetadata,
            CalendarSynchronizationType.Strict => Translator.Exception_FailedToSynchronizeCalendarData,
            _ => Translator.Exception_FailedToSynchronizeCalendarEvents
        };
    }

    private static string? FormatSynchronizationIssues(IEnumerable<SynchronizationIssue> issues)
    {
        if (issues == null)
        {
            return null;
        }

        var issueLines = issues
            .Where(issue => issue != null && !string.IsNullOrWhiteSpace(issue.Message))
            .Select(issue => string.IsNullOrWhiteSpace(issue.ScopeName)
                ? issue.Message
                : string.Format(Translator.SynchronizationIssueFormat_WithScope, issue.ScopeName, issue.Message))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToList();

        return issueLines.Count == 0 ? null : string.Join(Environment.NewLine, issueLines);
    }

    private void PreferencesServiceChanged(object? sender, string propertyName)
    {
        if (propertyName == nameof(IPreferencesService.EmailSyncIntervalMinutes))
        {
            RestartAutoSynchronizationLoop();
            return;
        }

        if (propertyName == nameof(IPreferencesService.IsSystemTrayIconEnabled))
        {
            UpdateTrayIconState(allowCreation: true);
        }
    }

    private void RestartAutoSynchronizationLoop()
    {
        if (_preferencesService == null)
            return;

        StopAutoSynchronizationLoop();

        int intervalMinutes = Math.Max(1, _preferencesService.EmailSyncIntervalMinutes);
        _autoSynchronizationLoopCts = new CancellationTokenSource();

        _ = RunAutoSynchronizationLoopAsync(TimeSpan.FromMinutes(intervalMinutes), _autoSynchronizationLoopCts.Token);
        LogActivation($"Automatic sync loop started. Interval: {intervalMinutes} minute(s).");
    }

    private void StopAutoSynchronizationLoop()
    {
        if (_autoSynchronizationLoopCts == null)
            return;

        _autoSynchronizationLoopCts.Cancel();
        _autoSynchronizationLoopCts.Dispose();
        _autoSynchronizationLoopCts = null;
    }

    private async Task LoadInitialWinoAccountAsync()
    {
        var winoAccountProfileService = Services.GetRequiredService<IWinoAccountProfileService>();
        var winoAccount = await winoAccountProfileService.GetActiveAccountAsync();

        if (winoAccount != null)
        {
            WeakReferenceMessenger.Default.Send(new WinoAccountProfileUpdatedMessage(winoAccount));
        }
    }

    private async Task RunAutoSynchronizationLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAutoSynchronizationAsync(cancellationToken);

            using var timer = new PeriodicTimer(interval);

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await ExecuteAutoSynchronizationAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // no-op
        }
        catch (Exception ex)
        {
            LogActivation($"Automatic sync loop failed: {ex.Message}");
        }
    }

    private async Task ExecuteAutoSynchronizationAsync(CancellationToken cancellationToken)
    {
        if (_synchronizationManager == null || _accountService == null)
            return;

        bool lockTaken = false;

        try
        {
            lockTaken = await _autoSynchronizationSemaphore.WaitAsync(0, cancellationToken);
            if (!lockTaken)
                return;

            var accounts = await _accountService.GetAccountsAsync();
            var currentAccountIds = accounts.Select(a => a.Id).ToHashSet();
            foreach (var staleAccountId in _inboxSyncCounters.Keys.Where(a => !currentAccountIds.Contains(a)).ToList())
            {
                _inboxSyncCounters.TryRemove(staleAccountId, out _);
            }

            var synchronizationTasks = accounts
                .Select(account => ExecuteAutoSynchronizationForAccountAsync(account, cancellationToken))
                .ToList();

            await Task.WhenAll(synchronizationTasks);
        }
        finally
        {
            if (lockTaken)
            {
                _autoSynchronizationSemaphore.Release();
            }
        }
    }

    private async Task ExecuteAutoSynchronizationForAccountAsync(Wino.Core.Domain.Entities.Shared.MailAccount account, CancellationToken cancellationToken)
    {
        if (_synchronizationManager == null)
            return;

        cancellationToken.ThrowIfCancellationRequested();

        if (_synchronizationManager.IsAccountSynchronizing(account.Id))
            return;

        var inboxSyncOptions = new MailSynchronizationOptions
        {
            AccountId = account.Id,
            Type = MailSynchronizationType.InboxOnly
        };

        var inboxSyncResult = await _synchronizationManager.SynchronizeMailAsync(inboxSyncOptions, cancellationToken);

        if (inboxSyncResult.CompletedState is SynchronizationCompletedState.Success or SynchronizationCompletedState.PartiallyCompleted)
        {
            await ClearInvalidCredentialAttentionIfNeededAsync(account.Id);

            var inboxSyncCount = _inboxSyncCounters.AddOrUpdate(account.Id, 1, (_, currentCount) => currentCount + 1);

            if (inboxSyncCount >= InboxSyncsPerFullSync)
            {
                var fullSyncOptions = new MailSynchronizationOptions
                {
                    AccountId = account.Id,
                    Type = MailSynchronizationType.FullFolders
                };

                await _synchronizationManager.SynchronizeMailAsync(fullSyncOptions, cancellationToken);
                _inboxSyncCounters[account.Id] = 0;
            }
        }

        if (!account.IsCalendarAccessGranted)
            return;

        var calendarOptions = new CalendarSynchronizationOptions
        {
            AccountId = account.Id,
            Type = CalendarSynchronizationType.CalendarMetadata
        };

        await _synchronizationManager.SynchronizeCalendarAsync(calendarOptions, cancellationToken);
    }

    private async Task ClearInvalidCredentialAttentionIfNeededAsync(Guid accountId)
    {
        if (_accountService == null)
            return;

        var account = await _accountService.GetAccountAsync(accountId);

        if (account?.AttentionReason != AccountAttentionReason.InvalidCredentials)
            return;

        await _accountService.ClearAccountAttentionAsync(accountId);
    }

    /// <summary>
    /// Handles activation redirected from another instance (single-instancing).
    /// This is called when a second instance tries to launch and redirects to this existing instance.
    /// </summary>
    public void HandleRedirectedActivation(AppActivationArguments args)
    {
        async Task HandleRedirectedActivationAsync()
        {
            await EnsureActivationInfrastructureAsync();

            // Handle different activation kinds
            if (args.Kind == ExtendedActivationKind.AppNotification)
            {
                // Handle toast notification activation
                var toastArgs = (AppNotificationActivatedEventArgs)args.Data;
                LogActivation($"Processing redirected notification activation. Arguments: {toastArgs.Argument}");
                await HandleToastActivationAsync(toastArgs.Argument, toastArgs.UserInput);
            }
            else if (args.Kind == ExtendedActivationKind.ShareTarget)
            {
                LogActivation("Processing redirected share target activation.");
                await HandleShareTargetActivationAsync(args, activateWindow: true);
            }
            else
            {
                var shouldActivateWindow = true;

                if (MainWindow is IWinoShellWindow shellWindow)
                {
                    if (args.Kind == ExtendedActivationKind.Launch &&
                        args.Data is ILaunchActivatedEventArgs launchArgs)
                    {
                        if (ToastActivationResolver.TryParse(launchArgs.Arguments, out var launchToastArguments))
                        {
                            shouldActivateWindow = ToastActivationResolver.ShouldBringToForeground(launchToastArguments);
                            LogActivation($"Processing redirected toast launch activation. Arguments: {launchArgs.Arguments}");
                            await HandleToastActivationAsync(launchToastArguments);
                        }
                        else
                        {
                            var launchArguments = launchArgs.Arguments;

                            if (Program.TryConsumeRedirectedAlternateModeOverride())
                            {
                                launchArguments = AppendLaunchArgument(launchArguments, ToggleDefaultModeLaunchArgument);
                            }

                            shellWindow.HandleAppActivation(launchArguments, launchArgs.TileId);
                        }
                    }
                    else if (TryResolveActivationMode(args, _preferencesService?.DefaultApplicationMode ?? WinoApplicationMode.Mail, out var redirectedMode))
                    {
                        shellWindow.HandleAppActivation(AppEntryConstants.GetModeLaunchArgument(redirectedMode));
                    }
                }

                // Redirected launches can target a shell window that is currently hidden in the tray.
                // Restore it through the window manager so Show/BringToFront/Activate happen together.
                if (shouldActivateWindow && MainWindow is WindowEx mainWindow)
                {
                    Services.GetRequiredService<IWinoWindowManager>().ActivateWindow(mainWindow);
                }
            }
        }

        // Dispatch to UI thread since this is called from Program.OnActivated.
        if (TryEnqueueActivationOnUiThread(() => _ = HandleRedirectedActivationAsync()))
            return;

        _ = HandleRedirectedActivationAsync();
    }

    private bool TryCreateNotificationActivationRoute(NotificationArguments toastArguments,
                                                      IDictionary<string, string>? userInput,
                                                      out NotificationActivationRoute route)
    {
        if (toastArguments.TryGetValue(Constants.ToastStoreUpdateActionKey, out string storeUpdateAction) &&
            storeUpdateAction == Constants.ToastStoreUpdateActionInstall)
        {
            route = new NotificationActivationRoute(true, HandleStoreUpdateToastAsync);
            return true;
        }

        if (toastArguments.TryGetValue(Constants.ToastDismissActionKey, out string _))
        {
            route = new NotificationActivationRoute(false, () =>
            {
                LogActivation("Handling notification dismiss action.");
                return Task.CompletedTask;
            });
            return true;
        }

        if (toastArguments.TryGetValue(Constants.ToastCalendarActionKey, out string calendarAction) &&
            toastArguments.TryGetValue(Constants.ToastCalendarItemIdKey, out string calendarItemIdString) &&
            Guid.TryParse(calendarItemIdString, out Guid calendarItemId))
        {
            route = calendarAction switch
            {
                Constants.ToastCalendarNavigateAction => new NotificationActivationRoute(true, () => HandleCalendarToastNavigationAsync(calendarItemId)),
                Constants.ToastCalendarSnoozeAction => new NotificationActivationRoute(false, () => HandleCalendarToastSnoozeAsync(userInput, calendarItemId)),
                Constants.ToastCalendarJoinOnlineAction => new NotificationActivationRoute(false, () => HandleCalendarToastJoinOnlineAsync(calendarItemId)),
                _ => default
            };

            return route.ExecuteAsync != null;
        }

        if (toastArguments.TryGetValue(Constants.ToastActionKey, out MailOperation action) &&
            Guid.TryParse(toastArguments[Constants.ToastMailUniqueIdKey], out Guid mailItemUniqueId))
        {
            if (action == MailOperation.Navigate)
            {
                route = new NotificationActivationRoute(true, () => HandleToastNavigationAsync(mailItemUniqueId));
                return true;
            }

            if (IsComposeToastAction(action))
            {
                route = new NotificationActivationRoute(true, () => HandleToastComposeActionAsync(action, mailItemUniqueId));
                return true;
            }

            route = new NotificationActivationRoute(false, () => HandleToastActionAsync(action, mailItemUniqueId));
            return true;
        }

        route = default;
        return false;
    }

    private async Task<bool> HandlePendingBootstrapActivationAsync(PendingBootstrapActivation pendingBootstrapActivation)
    {
        if (pendingBootstrapActivation.Mode != WinoApplicationMode.Calendar)
            return false;

        var navigationArgs = new CalendarPageNavigationArgs
        {
            RequestDefaultNavigation = true
        };

        await EnsureShellWindowAsync(
            WinoApplicationMode.Calendar,
            activateWindow: true,
            activationParameter: navigationArgs);

        return true;
    }

    private static string AppendLaunchArgument(string? launchArguments, string launchArgument)
    {
        return string.IsNullOrWhiteSpace(launchArguments)
            ? launchArgument
            : $"{launchArguments} {launchArgument}";
    }

    private static bool TryResolveActivationMode(AppActivationArguments activationArgs, WinoApplicationMode defaultMode, out WinoApplicationMode mode)
    {
        mode = defaultMode;

        if (activationArgs.Kind == ExtendedActivationKind.Protocol &&
            activationArgs.Data is IProtocolActivatedEventArgs protocolArgs)
        {
            var scheme = protocolArgs.Uri?.Scheme;

            if (string.Equals(scheme, "webcal", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scheme, "webcals", StringComparison.OrdinalIgnoreCase))
            {
                mode = WinoApplicationMode.Calendar;
                return true;
            }

            if (string.Equals(scheme, "mailto", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scheme, "google.pw.oauth2", StringComparison.OrdinalIgnoreCase))
            {
                mode = WinoApplicationMode.Mail;
                return true;
            }

        }

        if (activationArgs.Kind == ExtendedActivationKind.ShareTarget)
        {
            mode = WinoApplicationMode.Mail;
            return true;
        }

        if (activationArgs.Kind == ExtendedActivationKind.File &&
            activationArgs.Data is IFileActivatedEventArgs fileArgs)
        {
            var fileItem = fileArgs.Files?.FirstOrDefault();
            var extension = Path.GetExtension(fileItem?.Name ?? string.Empty);

            if (string.Equals(extension, ".ics", StringComparison.OrdinalIgnoreCase))
            {
                mode = WinoApplicationMode.Calendar;
                return true;
            }

            if (string.Equals(extension, ".eml", StringComparison.OrdinalIgnoreCase))
            {
                mode = WinoApplicationMode.Mail;
                return true;
            }
        }

        if (activationArgs.Kind == ExtendedActivationKind.Launch &&
            activationArgs.Data is ILaunchActivatedEventArgs launchArgs)
        {
            mode = AppModeActivationResolver.Resolve(launchArgs.Arguments, launchArgs.TileId, null, defaultMode);
            return true;
        }

        return false;
    }

    private static string? GetCurrentLaunchTileId()
    {
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();

        if (activationArgs.Kind == ExtendedActivationKind.Launch &&
            activationArgs.Data is ILaunchActivatedEventArgs launchArgs)
        {
            return launchArgs.TileId;
        }

        return null;
    }

    private bool TryEnqueueActivationOnUiThread(Action action)
    {
        var dispatcherQueue = MainWindow?.DispatcherQueue;

        if (dispatcherQueue == null)
        {
            var windowManager = Services.GetService<IWinoWindowManager>();
            var currentWindow = windowManager?.ActiveWindow
                               ?? windowManager?.GetWindow(WinoWindowKind.Shell)
                               ?? windowManager?.GetWindow(WinoWindowKind.Welcome);

            dispatcherQueue = currentWindow?.DispatcherQueue;
        }

        if (dispatcherQueue == null)
            return false;

        if (dispatcherQueue.HasThreadAccess)
        {
            action();
            return true;
        }

        return dispatcherQueue.TryEnqueue(() => action());
    }

}


