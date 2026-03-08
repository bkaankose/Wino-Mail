using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using MimeKit.Cryptography;
using Windows.ApplicationModel.Activation;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Mail.Services;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Activation;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Mail.WinUI.Services;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.Server;
using Wino.Messaging.UI;
using Wino.Services;
using WinUIEx;
namespace Wino.Mail.WinUI;

public partial class App : WinoApplication,
    IRecipient<NewMailSynchronizationRequested>,
    IRecipient<NewCalendarSynchronizationRequested>,
    IRecipient<AccountCreatedMessage>,
    IRecipient<AccountRemovedMessage>,
    IRecipient<GetStartedFromWelcomeRequested>
{
    private const int InboxSyncsPerFullSync = 20;
    private const string ToggleDefaultModeLaunchArgument = "--mode=toggle-default";
    private ISynchronizationManager? _synchronizationManager;
    private IPreferencesService? _preferencesService;
    private IAccountService? _accountService;
    private bool _windowManagerConfigured;
    private CancellationTokenSource? _autoSynchronizationLoopCts;
    private readonly SemaphoreSlim _autoSynchronizationSemaphore = new(1, 1);
    private readonly Dictionary<Guid, int> _inboxSyncCounters = [];

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
        services.AddTransient<IProviderService, ProviderService>();
        services.AddSingleton<IAuthenticatorConfig, MailAuthenticatorConfiguration>();
        services.AddSingleton<IAccountCalendarStateService, AccountCalendarStateService>();
    }

    private void RegisterViewModels(IServiceCollection services)
    {
        services.AddSingleton(typeof(MailAppShellViewModel));
        services.AddSingleton(typeof(CalendarAppShellViewModel));

        services.AddTransient(typeof(MailListPageViewModel));
        services.AddTransient(typeof(MailRenderingPageViewModel));
        services.AddTransient(typeof(AccountManagementViewModel));
        services.AddTransient(typeof(WelcomePageViewModel));
        services.AddTransient(typeof(WelcomePageV2ViewModel));
        services.AddTransient(typeof(ProviderSelectionPageViewModel));
        services.AddTransient(typeof(AccountSetupProgressPageViewModel));
        services.AddTransient(typeof(SpecialImapCredentialsPageViewModel));
        services.AddSingleton(typeof(WelcomeWizardContext));

        services.AddTransient(typeof(ComposePageViewModel));
        services.AddTransient(typeof(IdlePageViewModel));

        services.AddTransient(typeof(EditAccountDetailsPageViewModel));
        services.AddTransient(typeof(ImapCalDavSettingsPageViewModel));
        services.AddTransient(typeof(AccountDetailsPageViewModel));
        services.AddTransient(typeof(SignatureManagementPageViewModel));
        services.AddTransient(typeof(MessageListPageViewModel));
        services.AddTransient(typeof(ReadComposePanePageViewModel));
        services.AddTransient(typeof(MergedAccountDetailsPageViewModel));
        services.AddTransient(typeof(LanguageTimePageViewModel));
        services.AddTransient(typeof(AppPreferencesPageViewModel));
        services.AddTransient(typeof(StoragePageViewModel));
        services.AddTransient(typeof(AliasManagementPageViewModel));
        services.AddTransient(typeof(ContactsPageViewModel));
        services.AddTransient(typeof(SignatureAndEncryptionPageViewModel));
        services.AddTransient(typeof(EmailTemplatesPageViewModel));
        services.AddTransient(typeof(CreateEmailTemplatePageViewModel));
        services.AddTransient(typeof(CalendarPageViewModel));
        services.AddTransient(typeof(CalendarSettingsPageViewModel));
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
    public bool IsAppRunning() => MainWindow != null;

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        // Always register notification callbacks.
        TryRegisterAppNotifications();

        // Initialize required services regardless of launch activation type.
        // All activation scenarios require these services to be ready.
        // Note: Theme service is initialized separately after window creation.
        await InitializeServicesAsync();

        _synchronizationManager = Services.GetRequiredService<ISynchronizationManager>();
        _preferencesService = Services.GetRequiredService<IPreferencesService>();
        _accountService = Services.GetRequiredService<IAccountService>();

        EnsureWindowManagerConfigured();

        var hasAnyAccount = (await _accountService.GetAccountsAsync()).Any();
        if (!IsStartupTaskLaunch() && !hasAnyAccount)
        {
            CreateWelcomeWindow();
            await NewThemeService.InitializeAsync();
            MainWindow?.Activate();
            LogActivation("Welcome window created and activated.");
            return;
        }

        _preferencesService.PreferenceChanged -= PreferencesServiceChanged;
        _preferencesService.PreferenceChanged += PreferencesServiceChanged;

        RestartAutoSynchronizationLoop();

        // Check if launched from toast notification.
        if (IsNotificationActivation(out AppNotificationActivatedEventArgs toastArgs))
        {
            await HandleToastActivationAsync(toastArgs);
            return;
        }

        // Check if launched by startup task.
        bool isStartupTaskLaunch = IsStartupTaskLaunch();

        // Create the window (needed for system tray icon even in startup task scenario).
        CreateWindow(args);

        // Initialize theme service after window creation.
        // Theme service requires the window to exist to properly load and apply themes.
        await NewThemeService.InitializeAsync();
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

    private void AppNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // AppNotification callbacks are not guaranteed to run on the UI thread.
        // Marshal toast handling to the window dispatcher before touching window APIs.
        if (MainWindow?.DispatcherQueue?.TryEnqueue(() => _ = HandleToastActivationAsync(args)) == true)
            return;

        _ = HandleToastActivationAsync(args);
    }

    private void TryRegisterAppNotifications()
    {
        var notificationManager = AppNotificationManager.Default;

        notificationManager.NotificationInvoked -= AppNotificationInvoked;
        notificationManager.NotificationInvoked += AppNotificationInvoked;

        try
        {
            notificationManager.Register();
        }
        catch (Exception ex)
        {
            LogActivation($"App notification registration failed: {ex.GetType().Name} - {ex.Message}");
        }
    }

    /// <summary>
    /// Handles toast notification activation scenarios.
    /// </summary>
    private async Task HandleToastActivationAsync(AppNotificationActivatedEventArgs toastArgs)
    {
        var toastArguments = ToastArguments.Parse(toastArgs.Argument);

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
                await HandleCalendarToastSnoozeAsync(toastArgs, calendarItemId);
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
        }
    }

    private async Task HandleStoreUpdateToastAsync()
    {
        if (!IsAppRunning())
        {
            await CreateAndActivateWindow(null!);
        }
        else
        {
            EnsureMainWindowVisibleAndForeground();
        }

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

        if (!IsAppRunning())
        {
            await CreateAndActivateWindow(null!);
        }
        else
        {
            EnsureMainWindowVisibleAndForeground();
        }

        navigationService.ChangeApplicationMode(Core.Domain.Enums.WinoApplicationMode.Calendar);
        navigationService.Navigate(WinoPage.EventDetailsPage, target);
    }

    private async Task HandleCalendarToastSnoozeAsync(AppNotificationActivatedEventArgs toastArgs, Guid calendarItemId)
    {
        if (!TryGetSnoozeDurationMinutes(toastArgs, out var snoozeDurationMinutes))
            return;

        var calendarService = Services.GetRequiredService<ICalendarService>();
        var snoozedUntilLocal = DateTime.Now.AddMinutes(snoozeDurationMinutes);

        await calendarService.SnoozeCalendarItemAsync(calendarItemId, snoozedUntilLocal).ConfigureAwait(false);
    }

    private static bool TryGetSnoozeDurationMinutes(AppNotificationActivatedEventArgs toastArgs, out int snoozeDurationMinutes)
    {
        snoozeDurationMinutes = 0;

        if (toastArgs.UserInput == null ||
            !toastArgs.UserInput.TryGetValue(Constants.ToastCalendarSnoozeDurationInputId, out var selectedValue) ||
            selectedValue == null)
        {
            return false;
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
        var navigationService = Services.GetRequiredService<INavigationService>();

        var account = await mailService.GetMailAccountByUniqueIdAsync(mailItemUniqueId);
        if (account == null) return;

        var mailItem = await mailService.GetSingleMailItemAsync(mailItemUniqueId);
        if (mailItem == null) return;

        var message = new AccountMenuItemExtended(mailItem.AssignedFolder.Id, mailItem);

        // Store navigation parameter in LaunchProtocolService so AppShell can pick it up.
        var launchProtocolService = Services.GetRequiredService<ILaunchProtocolService>();
        launchProtocolService.LaunchParameter = message;

        // Create window if not already created.
        if (!IsAppRunning())
        {
            // Pass null for args since we're handling toast navigation
            await CreateAndActivateWindow(null!);
            navigationService.ChangeApplicationMode(Core.Domain.Enums.WinoApplicationMode.Mail);
        }
        else
        {
            // App is already running - send message and bring window to front.
            navigationService.ChangeApplicationMode(Core.Domain.Enums.WinoApplicationMode.Mail);
            WeakReferenceMessenger.Default.Send(message);
            EnsureMainWindowVisibleAndForeground();
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
            Application.Current.Exit();
            return;
        }

        var package = new MailOperationPreperationRequest(action, mailItem);

        // Check if app is already running (has a window).
        if (IsAppRunning())
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
                Application.Current.Exit();
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
            Application.Current.Exit();
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
            .Navigate(WinoPage.ManageAccountsPage, null, NavigationReferenceFrame.ShellFrame, NavigationTransitionType.DrillIn);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates the main window without activating it.
    /// Used for both normal launch and startup task launch (tray only).
    /// </summary>
    private void CreateWindow(Microsoft.UI.Xaml.LaunchActivatedEventArgs? args)
    {
        LogActivation("Creating main window.");

        var windowManager = Services.GetRequiredService<IWinoWindowManager>();
        MainWindow = windowManager.CreateWindow(WinoWindowKind.Shell, () => new ShellWindow());
        InitializeNavigationDispatcher();

        if (MainWindow is not IWinoShellWindow shellWindow)
            throw new ArgumentException("MainWindow must implement IWinoShellWindow");

        windowManager.SetPrimaryNavigationFrame(WinoWindowKind.Shell, shellWindow.GetMainFrame());

        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();

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

        if (TryResolveActivationMode(activationArgs, _preferencesService?.DefaultApplicationMode ?? WinoApplicationMode.Mail, out var activationMode))
        {
            shellWindow.HandleAppActivation(GetModeLaunchArgument(activationMode));
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
            windowManager.SetPrimaryNavigationFrame(WinoWindowKind.Welcome, welcomeWindow.GetRootFrame());

        InitializeNavigationDispatcher();

        Services.GetRequiredService<INavigationService>()
            .Navigate(WinoPage.WelcomeHostPage, null, NavigationReferenceFrame.ShellFrame, NavigationTransitionType.None);
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

        if (syncResult.CompletedState == SynchronizationCompletedState.Failed ||
            syncResult.CompletedState == SynchronizationCompletedState.PartiallyCompleted)
        {
            var dialogService = Services.GetRequiredService<IMailDialogService>();
            var errorMessage = GetSynchronizationFailureMessage(message.Options.Type, syncResult.Exception?.Message);
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

        if (calendarSyncResult.CompletedState == SynchronizationCompletedState.Failed)
        {
            var dialogService = Services.GetRequiredService<IMailDialogService>();
            dialogService.InfoBarMessage(
                Translator.Info_SyncFailedTitle,
                Translator.Exception_FailedToSynchronizeFolders,
                InfoBarMessageType.Error);
        }
    }

    public void Receive(AccountCreatedMessage message)
    {
        var windowManager = Services.GetRequiredService<IWinoWindowManager>();

        // Only transition when the account was created from the WelcomeWindow.
        if (windowManager.GetWindow(WinoWindowKind.Welcome) == null)
            return;

        MainWindow?.DispatcherQueue?.TryEnqueue(async () =>
        {
            // Create and activate ShellWindow — ActiveWindowChanged fires and rebinds the dispatcher.
            CreateWindow(null);
            windowManager.HideWindow(WinoWindowKind.Welcome);
            await NewThemeService.ApplyThemeToActiveWindowAsync();
            MainWindow?.Activate();
            RestartAutoSynchronizationLoop();
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
            if (accounts.Any()) return;

            // All accounts removed — go back to welcome wizard from step 1
            Services.GetRequiredService<WelcomeWizardContext>().Reset();
            StopAutoSynchronizationLoop();
            CreateWelcomeWindow();
            windowManager.HideWindow(WinoWindowKind.Shell);
            await NewThemeService.ApplyThemeToActiveWindowAsync();
            MainWindow?.Activate();
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

    private static string GetSynchronizationFailureMessage(MailSynchronizationType synchronizationType, string? exceptionMessage)
    {
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

    private async Task RunAutoSynchronizationLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAutoSynchronizationAsync(cancellationToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(interval);

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await ExecuteAutoSynchronizationAsync(cancellationToken).ConfigureAwait(false);
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
            lockTaken = await _autoSynchronizationSemaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!lockTaken)
                return;

            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
            var currentAccountIds = accounts.Select(a => a.Id).ToHashSet();
            _inboxSyncCounters.Keys.Where(a => !currentAccountIds.Contains(a)).ToList().ForEach(a => _inboxSyncCounters.Remove(a));

            foreach (var account in accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_synchronizationManager.IsAccountSynchronizing(account.Id))
                    continue;

                var inboxSyncOptions = new MailSynchronizationOptions()
                {
                    AccountId = account.Id,
                    Type = MailSynchronizationType.InboxOnly
                };

                var inboxSyncResult = await _synchronizationManager.SynchronizeMailAsync(inboxSyncOptions, cancellationToken).ConfigureAwait(false);

                if (inboxSyncResult.CompletedState is SynchronizationCompletedState.Success or SynchronizationCompletedState.PartiallyCompleted)
                {
                    _inboxSyncCounters.TryAdd(account.Id, 0);
                    _inboxSyncCounters[account.Id]++;

                    if (_inboxSyncCounters[account.Id] >= InboxSyncsPerFullSync)
                    {
                        var fullSyncOptions = new MailSynchronizationOptions()
                        {
                            AccountId = account.Id,
                            Type = MailSynchronizationType.FullFolders
                        };

                        await _synchronizationManager.SynchronizeMailAsync(fullSyncOptions, cancellationToken).ConfigureAwait(false);
                        _inboxSyncCounters[account.Id] = 0;
                    }
                }

                if (!account.IsCalendarAccessGranted)
                    continue;

                var calendarOptions = new CalendarSynchronizationOptions()
                {
                    AccountId = account.Id,
                    Type = CalendarSynchronizationType.CalendarMetadata
                };

                await _synchronizationManager.SynchronizeCalendarAsync(calendarOptions, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (lockTaken)
            {
                _autoSynchronizationSemaphore.Release();
            }
        }
    }

    /// <summary>
    /// Handles activation redirected from another instance (single-instancing).
    /// This is called when a second instance tries to launch and redirects to this existing instance.
    /// </summary>
    public void HandleRedirectedActivation(AppActivationArguments args)
    {
        // Dispatch to UI thread since this is called from Program.OnActivated
        MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            // Handle different activation kinds
            if (args.Kind == ExtendedActivationKind.AppNotification)
            {
                // Handle toast notification activation
                var toastArgs = (AppNotificationActivatedEventArgs)args.Data;
                _ = HandleToastActivationAsync(toastArgs);
            }
            else
            {
                if (MainWindow is IWinoShellWindow shellWindow)
                {
                    if (args.Kind == ExtendedActivationKind.Launch &&
                        args.Data is ILaunchActivatedEventArgs launchArgs)
                    {
                        var launchArguments = launchArgs.Arguments;

                        if (Program.TryConsumeRedirectedAlternateModeOverride())
                        {
                            launchArguments = AppendLaunchArgument(launchArguments, ToggleDefaultModeLaunchArgument);
                        }

                        shellWindow.HandleAppActivation(launchArguments, launchArgs.TileId);
                    }
                    else if (TryResolveActivationMode(args, _preferencesService?.DefaultApplicationMode ?? WinoApplicationMode.Mail, out var redirectedMode))
                    {
                        shellWindow.HandleAppActivation(GetModeLaunchArgument(redirectedMode));
                    }
                }

                // Bring the existing window to front after handling redirected activation.
                MainWindow?.BringToFront();
                MainWindow?.Activate();
            }
        });
    }

    private static string GetModeLaunchArgument(WinoApplicationMode mode)
        => mode == WinoApplicationMode.Calendar ? "--mode=calendar" : "--mode=mail";

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


