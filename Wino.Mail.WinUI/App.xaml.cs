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
    private int _initialNotificationActivationHandled;
    private int _initialShareActivationHandled;
    private CancellationTokenSource? _autoSynchronizationLoopCts;
    private readonly SemaphoreSlim _autoSynchronizationSemaphore = new(1, 1);
    private readonly SemaphoreSlim _activationInfrastructureSemaphore = new(1, 1);
    private readonly ConcurrentDictionary<Guid, int> _inboxSyncCounters = [];
    private NativeTrayIcon? _trayIcon;

    internal bool IsExiting => _isExiting;

    public App()
    {
        InitializeComponent();

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        CryptographyContext.Register(typeof(WindowsSecureMimeContext));

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

        windowManager.HideWindow(shellWindow);
        if (ReferenceEquals(MainWindow, shellWindow))
        {
            MainWindow = null;
            InitializeNavigationDispatcher();
        }

        shellWindow.PrepareForClose();
        shellWindow.Close();
    }

    private async Task ActivateWindowAsync(WindowEx window)
    {
        var windowManager = Services.GetRequiredService<IWinoWindowManager>();
        MainWindow = window;
        windowManager.ActivateWindow(window);
        await NewThemeService.ApplyThemeToActiveWindowAsync();
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
        _trayIcon?.Dispose();
        _trayIcon = null;

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
        services.AddTransient(typeof(SignatureManagementPageViewModel));
        services.AddTransient(typeof(MessageListPageViewModel));
        services.AddTransient(typeof(ReadComposePanePageViewModel));
        services.AddTransient(typeof(MergedAccountDetailsPageViewModel));
        services.AddTransient(typeof(AppPreferencesPageViewModel));
        services.AddTransient(typeof(StoragePageViewModel));
        services.AddTransient(typeof(WinoAccountManagementPageViewModel));
        services.AddTransient(typeof(AliasManagementPageViewModel));
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
        if (_activationInfrastructureInitialized)
            return;

        await _activationInfrastructureSemaphore.WaitAsync();

        try
        {
            if (_activationInfrastructureInitialized)
                return;

            TryRegisterAppNotifications();

            await Services.GetRequiredService<ReleaseLocalAccountDataCleanupService>()
                .RunIfNeededAsync();

            await InitializeServicesAsync();

            _synchronizationManager = Services.GetRequiredService<ISynchronizationManager>();
            _preferencesService = Services.GetRequiredService<IPreferencesService>();
            _accountService = Services.GetRequiredService<IAccountService>();

            EnsureWindowManagerConfigured();
            EnsureTrayIconCreated();

            _hasConfiguredAccounts = (await _accountService.GetAccountsAsync()).Any();

            if (_hasConfiguredAccounts)
            {
                _preferencesService.PreferenceChanged -= PreferencesServiceChanged;
                _preferencesService.PreferenceChanged += PreferencesServiceChanged;
                RestartAutoSynchronizationLoop();
            }

            _activationInfrastructureInitialized = true;
        }
        finally
        {
            _activationInfrastructureSemaphore.Release();
        }
    }

    private bool TryMarkInitialNotificationActivationHandled()
        => Interlocked.Exchange(ref _initialNotificationActivationHandled, 1) == 0;

    private bool TryMarkInitialShareActivationHandled()
        => Interlocked.Exchange(ref _initialShareActivationHandled, 1) == 0;

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        await EnsureActivationInfrastructureAsync();

        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();

        if (activationArgs.Kind == ExtendedActivationKind.ShareTarget &&
            TryMarkInitialShareActivationHandled())
        {
            LogActivation("Processing share target activation from OnLaunched.");

            if (await HandleShareTargetActivationAsync(activationArgs, activateWindow: true))
                return;
        }

        var hasAnyAccount = _hasConfiguredAccounts;
        if (!IsStartupTaskLaunch() && !hasAnyAccount)
        {
            CreateWelcomeWindow();
            await NewThemeService.InitializeAsync();
            MainWindow?.Activate();
            LogActivation("Welcome window created and activated.");
            return;
        }

        // Check if launched from toast notification.
        if (IsNotificationActivation(out AppNotificationActivatedEventArgs toastArgs) &&
            TryMarkInitialNotificationActivationHandled())
        {
            LogActivation($"Processing notification activation from OnLaunched. Arguments: {toastArgs.Argument}");
            await HandleToastActivationAsync(toastArgs.Argument, toastArgs.UserInput);
            return;
        }

        if (ToastActivationResolver.TryParse(args.Arguments, out var launchToastArguments) &&
            TryMarkInitialNotificationActivationHandled())
        {
            LogActivation($"Processing toast launch activation from OnLaunched. Arguments: {args.Arguments}");
            await HandleToastActivationAsync(launchToastArguments);
            return;
        }

        // Check if launched by startup task.
        bool isStartupTaskLaunch = IsStartupTaskLaunch();

        if (isStartupTaskLaunch && !hasAnyAccount)
        {
            CreateWelcomeWindow();
        }
        else
        {
            CreateWindow(args);
        }

        // Initialize theme service after window creation.
        // Theme service requires the window to exist to properly load and apply themes.
        await NewThemeService.InitializeAsync();

        if (hasAnyAccount)
        {
            // Wino account loading and activation.
            await LoadInitialWinoAccountAsync();
        }

        LogActivation("Theme service initialized.");

        // If startup task launch, keep window hidden (system tray only).
        // Otherwise, activate the window normally.
        if (isStartupTaskLaunch)
        {
            LogActivation("Launched by startup task. Window created but hidden (system tray only).");
        }
        else
        {
            // Normal launch - show and activate the window.
            // The What's New dialog is shown from MailAppShellViewModel.OnNavigatedTo once XamlRoot is ready.
            MainWindow?.Activate();
            LogActivation("Window created and activated.");
        }
    }

    public async Task HandleInitialActivationAsync()
    {
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();

        if (activationArgs.Kind == ExtendedActivationKind.Launch &&
            activationArgs.Data is ILaunchActivatedEventArgs launchArgs &&
            ToastActivationResolver.TryParse(launchArgs.Arguments, out var launchToastArguments) &&
            TryMarkInitialNotificationActivationHandled())
        {
            LogActivation($"Processing initial toast launch activation from application startup. Arguments: {launchArgs.Arguments}");

            await EnsureActivationInfrastructureAsync();
            await HandleToastActivationAsync(launchToastArguments);
        }
    }

    private void AppNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (MainWindow?.DispatcherQueue?.TryEnqueue(() => _ = HandleToastActivationAsync(args.Argument, args.UserInput)) == true)
            return;

        LogActivation($"Processing notification activation from NotificationInvoked. Arguments: {args.Argument}");
        _ = HandleToastActivationAsync(args.Argument, args.UserInput);
    }

    private void TryRegisterAppNotifications()
    {
        // Classic targeted toasts use normal launch activation instead of COM toast activators.
    }

    /// <summary>
    /// Handles toast notification activation scenarios.
    /// </summary>
    private async Task HandleToastActivationAsync(NotificationArguments toastArguments, IDictionary<string, string>? userInput = null)
    {
        LogActivation("Handling app notification activation.");

        if (toastArguments.TryGetValue(Constants.ToastStoreUpdateActionKey, out string storeUpdateAction) &&
            storeUpdateAction == Constants.ToastStoreUpdateActionInstall)
        {
            await HandleStoreUpdateToastAsync();
            return;
        }

        // Check calendar reminder toast activation first.
        if (toastArguments.TryGetValue(Constants.ToastCalendarActionKey, out string calendarAction) &&
            toastArguments.TryGetValue(Constants.ToastCalendarItemIdKey, out string calendarItemIdString) &&
            Guid.TryParse(calendarItemIdString, out Guid calendarItemId))
        {
            if (calendarAction == Constants.ToastCalendarNavigateAction)
            {
                await HandleCalendarToastNavigationAsync(calendarItemId);
                return;
            }

            if (calendarAction == Constants.ToastCalendarSnoozeAction)
            {
                await HandleCalendarToastSnoozeAsync(toastArguments, userInput, calendarItemId);
                return;
            }

            if (calendarAction == Constants.ToastCalendarJoinOnlineAction)
            {
                await HandleCalendarToastJoinOnlineAsync(calendarItemId);
                return;
            }
        }

        // Check if this is a navigation toast (user clicked the notification).
        if (toastArguments.TryGetValue(Constants.ToastActionKey, out MailOperation action) &&
            Guid.TryParse(toastArguments[Constants.ToastMailUniqueIdKey], out Guid mailItemUniqueId))
        {
            if (action == MailOperation.Navigate)
            {
                // User clicked notification - create window if needed and navigate.
                await HandleToastNavigationAsync(mailItemUniqueId);
            }
            else
            {
                // User clicked action button (Mark as Read, Delete, etc.)
                // Execute action without window and exit.

                await HandleToastActionAsync(action, mailItemUniqueId);
            }

            return;
        }

        LogActivation("App notification activation did not match any known handler.");
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

    private static int? GetToastSnoozeDurationMinutes(NotificationArguments toastArguments, IDictionary<string, string>? userInput)
    {
        if (toastArguments.TryGetValue(Constants.ToastCalendarSnoozeDurationMinutesKey, out var snoozeDurationValue) &&
            int.TryParse(snoozeDurationValue, out var snoozeDurationMinutes) &&
            snoozeDurationMinutes > 0)
        {
            return snoozeDurationMinutes;
        }

        if (userInput != null &&
            userInput.TryGetValue(Constants.ToastCalendarSnoozeDurationInputId, out var selectedValue) &&
            int.TryParse(selectedValue, out snoozeDurationMinutes) &&
            snoozeDurationMinutes > 0)
        {
            return snoozeDurationMinutes;
        }

        return null;
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

    private async Task<IWinoShellWindow?> EnsureShellWindowAsync(WinoApplicationMode mode, bool activateWindow, bool suppressStartupFlows = true)
    {
        var windowManager = Services.GetRequiredService<IWinoWindowManager>();
        var navigationService = Services.GetRequiredService<INavigationService>();
        var shellWindow = windowManager.GetWindow(WinoWindowKind.Shell) as IWinoShellWindow;

        if (shellWindow == null)
        {
            LogActivation($"Creating shell window for {mode} activation.");

            CreateWindow(
                null,
                AppEntryConstants.GetModeLaunchArgument(mode),
                new ShellModeActivationContext
                {
                    SuppressStartupFlows = suppressStartupFlows
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
            navigationService.ChangeApplicationMode(mode, new ShellModeActivationContext
            {
                SuppressStartupFlows = suppressStartupFlows
            });
        }

        if (activateWindow && shellWindow is WindowEx window)
        {
            await ActivateWindowAsync(window);
        }

        return shellWindow;
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
        var navigationService = Services.GetRequiredService<INavigationService>();

        var calendarItem = await calendarService.GetCalendarItemAsync(calendarItemId);
        if (calendarItem == null)
            return;

        var target = new CalendarItemTarget(calendarItem, CalendarEventTargetType.Single);

        await EnsureShellWindowAsync(WinoApplicationMode.Calendar, activateWindow: true);

        navigationService.ChangeApplicationMode(Core.Domain.Enums.WinoApplicationMode.Calendar);
        navigationService.Navigate(WinoPage.EventDetailsPage, target);
    }

    private async Task<object?> TryCreateToastNavigationParameterAsync(NotificationArguments toastArguments)
    {
        if (toastArguments.TryGetValue(Constants.ToastCalendarActionKey, out string calendarAction) &&
            calendarAction == Constants.ToastCalendarNavigateAction &&
            toastArguments.TryGetValue(Constants.ToastCalendarItemIdKey, out string calendarItemIdString) &&
            Guid.TryParse(calendarItemIdString, out Guid calendarItemId))
        {
            var calendarService = Services.GetRequiredService<ICalendarService>();
            var calendarItem = await calendarService.GetCalendarItemAsync(calendarItemId);

            if (calendarItem != null)
            {
                return new CalendarItemTarget(calendarItem, CalendarEventTargetType.Single);
            }
        }

        return null;
    }

    private async Task<(WinoApplicationMode Mode, object? Parameter)?> TryResolveToastActivationTargetAsync(AppActivationArguments activationArgs)
    {
        NotificationArguments? toastArguments = null;

        if (activationArgs.Kind == ExtendedActivationKind.Launch &&
            activationArgs.Data is ILaunchActivatedEventArgs launchArgs &&
            ToastActivationResolver.TryParse(launchArgs.Arguments, out var launchToastArguments))
        {
            toastArguments = launchToastArguments;
        }
        else if (activationArgs.Kind == ExtendedActivationKind.AppNotification &&
                 activationArgs.Data is AppNotificationActivatedEventArgs appNotificationArgs &&
                 ToastActivationResolver.TryParse(appNotificationArgs.Argument, out var appNotificationToastArguments))
        {
            toastArguments = appNotificationToastArguments;
        }
        else if (activationArgs.Data is ToastNotificationActivatedEventArgs classicToastArgs &&
                 ToastActivationResolver.TryParse(classicToastArgs.Argument, out var classicToastArguments))
        {
            toastArguments = classicToastArguments;
        }

        if (toastArguments == null ||
            !ToastActivationResolver.TryResolveMode(toastArguments, out var mode))
        {
            return null;
        }

        return (mode, await TryCreateToastNavigationParameterAsync(toastArguments));
    }

    private async Task HandleCalendarToastSnoozeAsync(NotificationArguments toastArguments, IDictionary<string, string>? userInput, Guid calendarItemId)
    {
        if (!TryGetSnoozeDurationMinutes(toastArguments, userInput, out var snoozeDurationMinutes))
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

    private bool TryGetSnoozeDurationMinutes(NotificationArguments toastArguments, IDictionary<string, string>? userInput, out int snoozeDurationMinutes)
    {
        snoozeDurationMinutes = GetToastSnoozeDurationMinutes(toastArguments, userInput)
                                ?? _preferencesService?.DefaultSnoozeDurationInMinutes
                                ?? 0;

        return snoozeDurationMinutes > 0;
    }

    /// <summary>
    /// Handles toast notification click for navigation.
    /// Creates window if not running, sets up navigation parameter.
    /// </summary>
    private async Task HandleToastNavigationAsync(Guid mailItemUniqueId)
    {
        var mailService = Services.GetRequiredService<IMailService>();
        var navigationService = Services.GetRequiredService<INavigationService>();

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
        navigationService.ChangeApplicationMode(Core.Domain.Enums.WinoApplicationMode.Mail);

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

    /// <summary>
    /// Creates the main window and activates it.
    /// </summary>
    private async Task CreateAndActivateWindow(Microsoft.UI.Xaml.LaunchActivatedEventArgs? args)
    {
        CreateWindow(args);

        // Initialize theme service after window is created.
        await NewThemeService.InitializeAsync();

        if (MainWindow != null)
            Services.GetRequiredService<IWinoWindowManager>().ActivateWindow(MainWindow);

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
            message.Options.GroupedSynchronizationTrackingId));

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
            if (_preferencesService != null)
            {
                _preferencesService.PreferenceChanged -= PreferencesServiceChanged;
                _preferencesService.PreferenceChanged += PreferencesServiceChanged;
            }

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
        if (propertyName != nameof(IPreferencesService.EmailSyncIntervalMinutes))
            return;

        RestartAutoSynchronizationLoop();
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
                _ = HandleToastActivationAsync(toastArgs.Argument, toastArgs.UserInput);
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
                            _ = HandleToastActivationAsync(launchToastArguments);
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
                        var navigationService = Services.GetRequiredService<INavigationService>();
                        var toastActivationTarget = await TryResolveToastActivationTargetAsync(args);

                        if (toastActivationTarget is { Parameter: CalendarItemTarget calendarTarget })
                        {
                            navigationService.ChangeApplicationMode(toastActivationTarget.Value.Mode, new ShellModeActivationContext
                            {
                                SuppressStartupFlows = true,
                                Parameter = calendarTarget
                            });
                            navigationService.Navigate(WinoPage.EventDetailsPage, calendarTarget);
                        }
                        else
                        {
                            shellWindow.HandleAppActivation(AppEntryConstants.GetModeLaunchArgument(redirectedMode));
                        }
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

        if (MainWindow?.DispatcherQueue.TryEnqueue(() => _ = HandleRedirectedActivationAsync()) == true)
            return;

        _ = HandleRedirectedActivationAsync();
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

        if (activationArgs.Data is ToastNotificationActivatedEventArgs classicToastArgs &&
            ToastActivationResolver.TryParse(classicToastArgs.Argument, out var classicToastArguments) &&
            ToastActivationResolver.TryResolveMode(classicToastArguments, out mode))
        {
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

}


