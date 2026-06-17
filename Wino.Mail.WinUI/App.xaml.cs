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
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using MimeKit.Cryptography;
using Sentry;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Serilog;
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
using Wino.Messaging.Client.Shell;
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
    IRecipient<AccountUpdatedMessage>,
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
    private readonly AppNotificationHandler _notificationHandler;
    private readonly AppActivationHandler _activationHandler;
    private readonly DispatcherQueue? _applicationDispatcherQueue;
    private NativeTrayIcon? _trayIcon;
    private readonly record struct ShellWindowActivationResult(IWinoShellWindow? ShellWindow, bool WasCreated);

    internal bool IsExiting => _isExiting;

    internal bool ShouldKeepShellWindowAliveOnClose()
    {
        if (_isExiting)
            return false;

        var preferencesService = _preferencesService ?? Services.GetService<IPreferencesService>();

        return preferencesService?.AppCloseBehavior != AppCloseBehavior.Terminate;
    }

    internal bool TryExitApplicationOnShellWindowClose()
    {
        if (_isExiting)
            return true;

        if (ShouldKeepShellWindowAliveOnClose())
            return false;

        ExitApplication();

        return true;
    }

    public App()
    {
        _applicationDispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _notificationHandler = new AppNotificationHandler(this);
        _activationHandler = new AppActivationHandler(this, _notificationHandler);

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
        var activeWindow = windowManager.ActiveWindow;

        MainWindow = ReferenceEquals(activeWindow, window)
                     ? null
                     : activeWindow
                     ?? windowManager.GetWindow(WinoWindowKind.Shell)
                     ?? windowManager.GetWindow(WinoWindowKind.Welcome);

        if (window is IWinoShellWindow)
        {
            UpdateTrayIconState(allowCreation: !_isExiting);
        }

        InitializeNavigationDispatcher();
    }

    private void EnsureTrayIconCreated()
    {
        if (_trayIcon != null)
        {
            LogActivation("System tray icon creation skipped because an icon instance already exists.");
            return;
        }

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Wino_Icon.ico");
        var iconExists = File.Exists(iconPath);
        var appCloseBehavior = _preferencesService?.AppCloseBehavior ?? AppCloseBehavior.RunInBackgroundWithTrayIcon;

        LogActivation($"Creating system tray icon. IconPath: {iconPath}, IconExists: {iconExists}, AppCloseBehavior: {appCloseBehavior}, HasConfiguredAccounts: {_hasConfiguredAccounts}, OS: {Environment.OSVersion}");
        SentrySdk.AddBreadcrumb(
            "Creating system tray icon.",
            category: "system-tray",
            data: new Dictionary<string, string>
            {
                ["icon_path"] = iconPath,
                ["icon_exists"] = iconExists.ToString(),
                ["app_close_behavior"] = appCloseBehavior.ToString(),
                ["has_configured_accounts"] = _hasConfiguredAccounts.ToString(),
                ["os_version"] = Environment.OSVersion.ToString()
            });

        var dispatcherQueue = DispatcherQueue.GetForCurrentThread()
                             ?? throw new InvalidOperationException("Tray icon must be created on a thread with a DispatcherQueue.");

        NativeTrayIcon? trayIcon = null;
        try
        {
            trayIcon = new NativeTrayIcon(
                dispatcherQueue,
                iconPath,
                "Wino Mail",
                BuildTrayMenu,
                ActivatePreferredWindowAsync);
            trayIcon.Create();
            _trayIcon = trayIcon;
            LogActivation("System tray icon created successfully.");
            SentrySdk.AddBreadcrumb("System tray icon created successfully.", category: "system-tray");
        }
        catch (Exception ex)
        {
            trayIcon?.Dispose();
            Log.Error(ex, "Failed to create system tray icon. IconPath: {IconPath}, IconExists: {IconExists}, AppCloseBehavior: {AppCloseBehavior}, HasConfiguredAccounts: {HasConfiguredAccounts}, OS: {OSVersion}",
                iconPath,
                iconExists,
                appCloseBehavior,
                _hasConfiguredAccounts,
                Environment.OSVersion);

            LogInitializer.CaptureException(ex, "SystemTrayIconCreation", new Dictionary<string, string>
            {
                ["icon_path"] = iconPath,
                ["icon_exists"] = iconExists.ToString(),
                ["app_close_behavior"] = appCloseBehavior.ToString(),
                ["has_configured_accounts"] = _hasConfiguredAccounts.ToString(),
                ["os_version"] = Environment.OSVersion.ToString()
            });
        }
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon == null)
            return;

        LogActivation("Disposing system tray icon.");
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
           (_preferencesService?.AppCloseBehavior ?? AppCloseBehavior.RunInBackgroundWithTrayIcon) == AppCloseBehavior.RunInBackgroundWithTrayIcon;

    private void UpdateTrayIconState(bool allowCreation)
    {
        var shouldCreateTrayIcon = ShouldCreateTrayIcon();
        LogActivation($"Updating system tray icon state. AllowCreation: {allowCreation}, ShouldCreate: {shouldCreateTrayIcon}, HasConfiguredAccounts: {_hasConfiguredAccounts}, AppCloseBehavior: {_preferencesService?.AppCloseBehavior.ToString() ?? "Unknown"}");

        if (!allowCreation || !shouldCreateTrayIcon)
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

        return ActivateShellFromTrayAsync(WinoApplicationMode.Mail);
    }

    private Task OpenMailFromTrayAsync()
        => _hasConfiguredAccounts
            ? ActivateShellFromTrayAsync(WinoApplicationMode.Mail)
            : ActivateWelcomeWindowAsync();

    private Task OpenCalendarFromTrayAsync()
        => _hasConfiguredAccounts
            ? ActivateShellFromTrayAsync(WinoApplicationMode.Calendar)
            : ActivateWelcomeWindowAsync();

    private Task ActivateShellFromTrayAsync(WinoApplicationMode mode)
        => EnsureShellWindowAsync(mode, activateWindow: true);

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

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: false);
        });

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

            await TranslationService.InitializeAsync();

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
        await _activationHandler.HandleLaunchAsync(args, activationArgs);
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
    private Task HandleToastActivationAsync(NotificationArguments toastArguments, IDictionary<string, string>? userInput = null)
        => _notificationHandler.HandleActivationAsync(toastArguments, userInput);

    private Task HandleToastActivationAsync(string toastArgument, IDictionary<string, string>? userInput = null)
        => _notificationHandler.HandleActivationAsync(toastArgument, userInput);

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
            await ExecuteOnActivationUiThreadAsync(
                () => Services.GetRequiredService<MailAppShellViewModel>().HandlePendingShareRequestAsync());
        }

        return true;
    }

    private async Task<bool> HandleMailToProtocolActivationAsync(MailToUri mailToUri, bool activateWindow)
    {
        if (mailToUri == null)
            return false;

        Services.GetRequiredService<ILaunchProtocolService>().MailToUri = mailToUri;

        if (!_hasConfiguredAccounts)
            return false;

        await EnsureShellWindowAsync(
            WinoApplicationMode.Mail,
            activateWindow,
            suppressStartupFlows: true,
            activationParameter: mailToUri);

        return true;
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
        if (!HasActivationUiThreadAccess())
        {
            await ExecuteOnActivationUiThreadAsync(LaunchWelcomeWindowAsync);
            return;
        }

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
        if (!HasActivationUiThreadAccess())
        {
            return await ExecuteOnActivationUiThreadAsync(
                () => EnsureShellWindowAsync(mode, activateWindow, suppressStartupFlows, activationParameter));
        }

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
            navigationService.RestoreShell(mode, new ShellModeActivationContext
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
            await ExecuteOnActivationUiThreadAsync(() =>
            {
                EnsureMainWindowVisibleAndForeground();
                return Task.CompletedTask;
            });

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

    private void CompleteStartupTaskLaunch(bool hasAnyAccount)
    {
        if (!hasAnyAccount)
        {
            LogActivation("Launched by startup task without configured accounts. Exiting without creating a window.");
            ExitApplication();
            return;
        }

        _ = ExecuteOnActivationUiThreadAsync(() =>
        {
            UpdateTrayIconState(allowCreation: true);
            return Task.CompletedTask;
        });

        LogActivation("Launched by startup task. Running in background without creating a window.");
    }

    private async Task CompleteStandardLaunchAsync(Microsoft.UI.Xaml.LaunchActivatedEventArgs args,
                                                   bool hasAnyAccount)
    {
        if (!HasActivationUiThreadAccess())
        {
            await ExecuteOnActivationUiThreadAsync(() => CompleteStandardLaunchAsync(args, hasAnyAccount));
            return;
        }

        CreateWindow(args);

        await NewThemeService.InitializeAsync();

        if (hasAnyAccount)
        {
            await LoadInitialWinoAccountAsync();
        }

        LogActivation("Theme service initialized.");

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
            await ExecuteOnActivationUiThreadAsync(() =>
            {
                WeakReferenceMessenger.Default.Send(message);
                launchProtocolService.LaunchParameter = null;
                return Task.CompletedTask;
            });
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

        await ExecuteOnActivationUiThreadAsync(async () =>
        {
            if (mailShellViewModel.MenuItems.TryGetAccountMenuItem(account.Id, out IAccountMenuItem accountMenuItem))
            {
                await mailShellViewModel.ChangeLoadedAccountAsync(accountMenuItem, navigateInbox: false);
            }

            if (mailShellViewModel.MenuItems.TryGetSpecialFolderMenuItem(account.Id, SpecialFolderType.Draft, out var draftFolderMenuItem))
            {
                await mailShellViewModel.NavigateFolderAsync(draftFolderMenuItem);
            }
        });

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

        await ExecuteOnActivationUiThreadAsync(() =>
        {
            navigationService.Navigate(WinoPage.ComposePage,
                                       new MailItemViewModel(draftMailCopy),
                                       NavigationReferenceFrame.RenderingFrame,
                                       NavigationTransitionType.DrillIn);

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Creates the main window and activates it.
    /// </summary>
    private async Task CreateAndActivateWindow(Microsoft.UI.Xaml.LaunchActivatedEventArgs? args)
    {
        if (!HasActivationUiThreadAccess())
        {
            await ExecuteOnActivationUiThreadAsync(() => CreateAndActivateWindow(args));
            return;
        }

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

        var navigationService = Services.GetRequiredService<INavigationService>();
        var defaultMode = WinoApplicationMode.Mail;
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();

        if (activationContextOverride != null)
        {
            var targetMode = !string.IsNullOrWhiteSpace(forcedLaunchArguments)
                ? AppModeActivationResolver.Resolve(forcedLaunchArguments, null, null, defaultMode)
                : TryResolveActivationMode(activationArgs, defaultMode, out var resolvedActivationMode)
                    ? resolvedActivationMode
                    : AppModeActivationResolver.Resolve(args?.Arguments, GetCurrentLaunchTileId(), Environment.CommandLine, defaultMode);

            ApplyShellWindowTaskbarIdentity(shellWindow, targetMode);
            navigationService.RestoreShell(targetMode, activationContextOverride);
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
        WeakReferenceMessenger.Default.Register<AccountUpdatedMessage>(this);
        WeakReferenceMessenger.Default.Register<GetStartedFromWelcomeRequested>(this);
        WeakReferenceMessenger.Default.Register<WelcomeImportCompletedMessage>(this);
        WeakReferenceMessenger.Default.Register<LanguageChanged>(this);
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

            if (message.Options.Type is MailSynchronizationType.FullFolders or MailSynchronizationType.FoldersOnly)
            {
                QueueJumpListOptionsUpdateOnUiThread();
            }
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
        QueueJumpListOptionsUpdateOnUiThread();

        var windowManager = Services.GetRequiredService<IWinoWindowManager>();

        // Only transition when the account was created from the WelcomeWindow.
        if (windowManager.GetWindow(WinoWindowKind.Welcome) == null)
        {
            EnsureAutoSynchronizationLoop();
            QueueCreatedAccountSynchronization(message.Account);
            return;
        }

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

    private void QueueCreatedAccountSynchronization(Wino.Core.Domain.Entities.Shared.MailAccount account)
    {
        if (account.IsMailAccessGranted)
        {
            WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = account.Id,
                Type = MailSynchronizationType.FullFolders
            }));
        }

        if (account.IsCalendarAccessGranted)
        {
            WeakReferenceMessenger.Default.Send(new NewCalendarSynchronizationRequested(new CalendarSynchronizationOptions
            {
                AccountId = account.Id,
                Type = CalendarSynchronizationType.CalendarEvents
            }));
        }
    }

    private void EnsureAutoSynchronizationLoop()
    {
        if (_autoSynchronizationLoopCts != null)
            return;

        RestartAutoSynchronizationLoop();
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
            await UpdateJumpListOptionsSafeAsync();

            Services.GetRequiredService<IMailDialogService>().InfoBarMessage(
                Translator.GeneralTitle_Info,
                Translator.WinoAccount_Management_ImportReloginReminder,
                InfoBarMessageType.Information);
        });
    }

    public void Receive(AccountRemovedMessage message)
    {
        QueueJumpListOptionsUpdateOnUiThread();

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

    public void Receive(AccountUpdatedMessage message)
        => QueueJumpListOptionsUpdateOnUiThread();

    private void QueueJumpListOptionsUpdateOnUiThread()
        => TryEnqueueActivationOnUiThread(() => _ = UpdateJumpListOptionsSafeAsync());

    private async Task UpdateJumpListOptionsSafeAsync()
    {
        try
        {
            await Services.GetRequiredService<INotificationBuilder>().UpdateJumpListOptionsAsync();
        }
        catch
        {
        }
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

        if (propertyName is nameof(IPreferencesService.AppCloseBehavior) or nameof(IPreferencesService.IsSystemTrayIconEnabled))
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
    public async void HandleRedirectedActivation(AppActivationArguments args)
    {
        try
        {
            await _activationHandler.HandleRedirectedActivationAsync(args);
        }
        catch (Exception ex)
        {
            LogActivation($"Redirected activation failed: {ex.GetType().Name} - {ex.Message}");
        }
    }

    private async Task ActivateRedirectedShellAsync(RedirectedActivationRoute route)
    {
        if (!HasActivationUiThreadAccess())
        {
            await ExecuteOnActivationUiThreadAsync(() => ActivateRedirectedShellAsync(route));
            return;
        }

        var windowManager = Services.GetRequiredService<IWinoWindowManager>();
        var shellWindow = MainWindow as IWinoShellWindow
                          ?? windowManager.GetWindow(WinoWindowKind.Shell) as IWinoShellWindow;
        var shellActivationHandled = false;
        var shellActivationAppId = AppEntryConstants.GetAppUserModelId(route.ActivationMode);

        if (!string.IsNullOrWhiteSpace(route.ShellActivationArguments) && shellWindow != null)
        {
            shellWindow.HandleAppActivation(route.ShellActivationArguments, route.ShellActivationTileId, shellActivationAppId);
            shellActivationHandled = true;
        }

        if (route.ShouldActivateWindow && shellWindow == null && _hasConfiguredAccounts)
        {
            var result = await EnsureShellWindowAsync(route.ActivationMode, activateWindow: true);
            shellWindow = result.ShellWindow;
            route = route with { ShouldActivateWindow = false };
        }

        if (!shellActivationHandled &&
            !string.IsNullOrWhiteSpace(route.ShellActivationArguments) &&
            shellWindow != null)
        {
            shellWindow.HandleAppActivation(route.ShellActivationArguments, route.ShellActivationTileId, shellActivationAppId);
        }

        // Redirected launches can target a shell window that is currently hidden in the tray.
        // Restore it through the window manager so Show/BringToFront/Activate happen together.
        var activationWindow = shellWindow as WindowEx
                               ?? windowManager.GetWindow(WinoWindowKind.Welcome)
                               ?? MainWindow;

        if (route.ShouldActivateWindow && activationWindow != null)
        {
            MainWindow = activationWindow;
            windowManager.ActivateWindow(activationWindow);
        }
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
            mode = AppModeActivationResolver.Resolve(launchArgs.Arguments, launchArgs.TileId, Environment.CommandLine, defaultMode);
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

    private bool HasActivationUiThreadAccess()
        => GetActivationDispatcherQueue()?.HasThreadAccess == true;

    private DispatcherQueue? GetActivationDispatcherQueue()
    {
        var windowManager = Services.GetService<IWinoWindowManager>();
        var currentWindow = windowManager?.ActiveWindow
                           ?? windowManager?.GetWindow(WinoWindowKind.Shell)
                           ?? windowManager?.GetWindow(WinoWindowKind.Welcome);

        return currentWindow?.DispatcherQueue
               ?? MainWindow?.DispatcherQueue
               ?? _applicationDispatcherQueue;
    }

    private Task ExecuteOnActivationUiThreadAsync(Func<Task> action)
        => ExecuteOnActivationUiThreadAsync(async () =>
        {
            await action();
            return true;
        });

    private Task<T> ExecuteOnActivationUiThreadAsync<T>(Func<Task<T>> action)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        var dispatcherQueue = GetActivationDispatcherQueue()
                              ?? throw new InvalidOperationException("Activation UI dispatcher is not available.");

        if (dispatcherQueue.HasThreadAccess)
            return action();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                completion.SetResult(await action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }))
        {
            completion.SetException(new InvalidOperationException("Failed to enqueue activation work on the UI dispatcher."));
        }

        return completion.Task;
    }

    private bool TryEnqueueActivationOnUiThread(Action action)
    {
        var dispatcherQueue = GetActivationDispatcherQueue();

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


