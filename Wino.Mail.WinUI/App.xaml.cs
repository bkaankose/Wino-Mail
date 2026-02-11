using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using MimeKit.Cryptography;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Mail.Services;
using Wino.Mail.ViewModels;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Services;
using Wino.Messaging.UI;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.Server;
using Wino.Services;
namespace Wino.Mail.WinUI;

public partial class App : WinoApplication,
    IRecipient<NewMailSynchronizationRequested>,
    IRecipient<NewCalendarSynchronizationRequested>
{
    private ISynchronizationManager? _synchronizationManager;

    public App()
    {
        InitializeComponent();

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        CryptographyContext.Register(typeof(WindowsSecureMimeContext));

        RegisterRecipients();
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

        services.AddTransient(typeof(ComposePageViewModel));
        services.AddTransient(typeof(IdlePageViewModel));

        services.AddTransient(typeof(EditAccountDetailsPageViewModel));
        services.AddTransient(typeof(AccountDetailsPageViewModel));
        services.AddTransient(typeof(SignatureManagementPageViewModel));
        services.AddTransient(typeof(MessageListPageViewModel));
        services.AddTransient(typeof(ReadComposePanePageViewModel));
        services.AddTransient(typeof(MergedAccountDetailsPageViewModel));
        services.AddTransient(typeof(LanguageTimePageViewModel));
        services.AddTransient(typeof(AppPreferencesPageViewModel));
        services.AddTransient(typeof(AliasManagementPageViewModel));
        services.AddTransient(typeof(ContactsPageViewModel));
        services.AddTransient(typeof(SignatureAndEncryptionPageViewModel));
        services.AddTransient(typeof(CalendarPageViewModel));
        services.AddTransient(typeof(CalendarSettingsPageViewModel));
        services.AddTransient(typeof(CalendarAccountSettingsPageViewModel));
        services.AddTransient(typeof(EventDetailsPageViewModel));
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

        AppNotificationManager notificationManager = AppNotificationManager.Default;

        notificationManager.NotificationInvoked -= AppNotificationInvoked;
        notificationManager.NotificationInvoked += AppNotificationInvoked;
        notificationManager.Register();

        // Initialize required services regardless of launch activation type.
        // All activation scenarios require these services to be ready.
        // Note: Theme service is initialized separately after window creation.
        await InitializeServicesAsync();

        _synchronizationManager = Services.GetRequiredService<ISynchronizationManager>();

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
            // Window is created but not activated. User can show it from system tray.
        }
        else
        {
            // Normal launch - show and activate the window.
            MainWindow.Activate();
            LogActivation("Window created and activated.");
        }
    }

    private async void AppNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
        => await HandleToastActivationAsync(args);

    /// <summary>
    /// Handles toast notification activation scenarios.
    /// </summary>
    private async Task HandleToastActivationAsync(AppNotificationActivatedEventArgs toastArgs)
    {
        var toastArguments = ToastArguments.Parse(toastArgs.Argument);

        // Check calendar reminder toast activation first.
        if (toastArguments.TryGetValue(Constants.ToastCalendarActionKey, out string calendarAction) &&
            calendarAction == Constants.ToastCalendarNavigateAction &&
            toastArguments.TryGetValue(Constants.ToastCalendarItemIdKey, out string calendarItemIdString) &&
            Guid.TryParse(calendarItemIdString, out Guid calendarItemId))
        {
            await HandleCalendarToastNavigationAsync(calendarItemId);
            return;
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

    private async Task HandleCalendarToastNavigationAsync(Guid calendarItemId)
    {
        var calendarService = Services.GetRequiredService<ICalendarService>();
        var navigationService = Services.GetRequiredService<INavigationService>();

        var calendarItem = await calendarService.GetCalendarItemAsync(calendarItemId).ConfigureAwait(false);
        if (calendarItem == null)
            return;

        var target = new CalendarItemTarget(calendarItem, CalendarEventTargetType.Single);

        if (!IsAppRunning())
        {
            await CreateAndActivateWindow(null!);
        }
        else
        {
            MainWindow.BringToFront();
            MainWindow.Activate();
        }

        navigationService.ChangeApplicationMode(Core.Domain.Enums.WinoApplicationMode.Calendar);
        navigationService.Navigate(WinoPage.EventDetailsPage, target);
    }

    /// <summary>
    /// Handles toast notification click for navigation.
    /// Creates window if not running, sets up navigation parameter.
    /// </summary>
    private async Task HandleToastNavigationAsync(Guid mailItemUniqueId)
    {
        var mailService = Services.GetRequiredService<IMailService>();

        var account = await mailService.GetMailAccountByUniqueIdAsync(mailItemUniqueId).ConfigureAwait(false);
        if (account == null) return;

        var mailItem = await mailService.GetSingleMailItemAsync(mailItemUniqueId).ConfigureAwait(false);
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
        }
        else
        {
            // App is already running - send message and bring window to front.
            WeakReferenceMessenger.Default.Send(message);
            MainWindow.BringToFront();
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
    private async Task CreateAndActivateWindow(LaunchActivatedEventArgs args)
    {
        CreateWindow(args);

        // Initialize theme service after window is created.
        await NewThemeService.InitializeAsync();

        MainWindow.Activate();
        LogActivation("Window created and activated.");
    }

    /// <summary>
    /// Creates the main window without activating it.
    /// Used for both normal launch and startup task launch (tray only).
    /// </summary>
    private void CreateWindow(LaunchActivatedEventArgs args)
    {
        LogActivation("Creating main window.");

        MainWindow = new ShellWindow();

        var nativeAppService = Services.GetRequiredService<INativeAppService>();
        nativeAppService.GetCoreWindowHwnd = () => WinRT.Interop.WindowNative.GetWindowHandle(MainWindow);

        if (MainWindow is not IWinoShellWindow shellWindow)
            throw new ArgumentException("MainWindow must implement IWinoShellWindow");

        shellWindow.HandleAppActivation(args);
    }

    private void RegisterRecipients()
    {
        WeakReferenceMessenger.Default.Register<NewMailSynchronizationRequested>(this);
        WeakReferenceMessenger.Default.Register<NewCalendarSynchronizationRequested>(this);
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
                // For other activation types (Launch, Protocol, etc.), bring window to front
                MainWindow?.BringToFront();
                MainWindow?.Activate();
            }
        });
    }

}
