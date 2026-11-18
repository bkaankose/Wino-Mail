using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI.Activation;
using Wino.Mail.WinUI.Extensions;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Services;
using Wino.Mail.WinUI.Services.WidgetsAndTiles;
using Wino.Services;
using Wino.Services.Security;
using Wino.Platform.Windows;
using Wino.PushRelay.Socket;
using Wino.PushRelay.Socket.KeyStores;
using Wino.Authentication;
using Wino.Authentication.OAuth;
using Wino.Core.Domain.Models.Platform;

namespace Wino.Mail.WinUI;

public static class CoreUWPContainerSetup
{
    public static void RegisterCoreUWPServices(this IServiceCollection services)
    {
        services.AddSingleton<IApplicationResourceManager<ResourceDictionary>, ApplicationResourceManager>();
        services.AddSingleton<WinUIDispatcher>();
        services.AddSingleton<IDispatcher>(provider => provider.GetRequiredService<WinUIDispatcher>());

        services.AddSingleton<IWinoWindowManager, WinoWindowManager>();
        services.AddSingleton<IWindowsMainWindowHost, WindowsMainWindowHost>();
        services.AddSingleton<IWindowsPrintService, PrintService>();
        // Capture the host UI dispatcher before background initialization resolves it.
        var platformDispatcher = DispatcherQueue.GetForCurrentThread();
        services.AddWindowsPlatformServices(new NativeAppService(
            platformDispatcher == null ? null : new DispatcherQueueThreadQueue(platformDispatcher),
            SessionStartActivation.SyncRegistrationAsync,
            WebViewExtensions.GetSharedEnvironmentAsync));
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddSingleton<WindowsShellPreferences>();
        services.AddSingleton<WidgetsAndTilesService>();
        // DPAPI-protected secure items under LocalState\secure; earlier releases' plaintext columns and files move there at startup.
        services.AddProtectedFileCredentialStorage();
        services.AddSingleton<IPlatformCapabilities>(_ => new PlatformCapabilities(
            Printing: Windows.Graphics.Printing.PrintManager.IsSupported(), PdfExport: true,
            Smime: true, AdditionalWindows: true, Notifications: true, NotificationActions: true,
            StartupIntegration: true, Tray: true, GlobalHotkeys: true, GeneralActivation: true,
            MicrosoftStore: true, RemoteNotifications: false, KeyboardShortcuts: true, FileBackup: true, Purchases: true,
            AppModeTaskbarIdentity: true, PointerHover: true, MailListDensity: true, PresentationDetection: true,
            ConfigurableSyncIntervals: true, BadgeLaunchNavigation: true, AddressFirstAccountSetup: true, WidgetsAndLiveTiles: true,
            AttachmentSecurityCheck: true, VoiceControl: true, AppLock: true));
        // The desktop socket (Wino.PushRelay.Socket): the key is a DPAPI-protected file. ApplicationRuntimeInitialization
        // starts the socket after the relay registration in background startup.
        services.AddSingleton<IPushRelaySocketKeyStore, ProtectedFilePushRelaySocketKeyStore>();
        services.AddWinoRelaySocketClient();
        services.AddSingleton(provider => new WindowsMigratingGoogleTokenStore(
            provider.GetRequiredService<ISecureItemStore>(),
            provider.GetRequiredService<ProtectedFileSecureItemStore>().ItemPath,
            new WindowsGoogleTokenStore(provider.GetRequiredService<IApplicationConfiguration>())));
        services.AddSingleton<IGoogleTokenStore>(provider => provider.GetRequiredService<WindowsMigratingGoogleTokenStore>());
        services.AddSingleton<ILegacyCredentialFileMigration>(provider => provider.GetRequiredService<WindowsMigratingGoogleTokenStore>());
        services.AddSingleton<IOAuthTokenStore, SecureItemOAuthTokenStore>();
        services.AddSingleton<INewThemeService, NewThemeService>();
        services.AddSingleton<IStatePersistanceService, StatePersistenceService>();

        // One dialog stack, one presentation semaphore: the base interface forwards to the mail dialog service.
        services.AddSingleton<IDialogServiceBase>(provider => provider.GetRequiredService<IMailDialogService>());
        services.AddSingleton<IWinoDialogService>(provider => (IWinoDialogService)provider.GetRequiredService<IMailDialogService>());
        services.AddSingleton<IExternalBrowserAuthenticationPresenter, ExternalBrowserAuthenticationPresenter>();

        // Exchange modern-auth sign-in: a WebView2 dialog that captures the redirect in-process, apart from every
        // system-browser flow in AddWindowsPlatformServices.
        services.AddSingleton<IEmbeddedBrowserAuthBroker>(provider => new WindowsEmbeddedBrowserAuthBroker(
            provider.GetRequiredService<IDispatcher>(), provider.GetRequiredService<Lazy<IWinoDialogService>>()));
        services.AddSingleton<IWinoAccountExternalSignInService>(provider => new WinoAccountExternalSignInService(
            provider.GetRequiredService<IWinoAccountApiClient>(), provider.GetRequiredService<IWinoAccountProfileService>(),
            apple: null, google: null, provider.GetRequiredService<IWinoAccountBrowserAuthBroker>()));
        global::Wino.Core.Providers.GmailProviderRegistration.AddLoopbackGoogleAuthorizationCodeReceiver(services);
        // Lazy edges break the two constructor cycles in the shell: the Store service reaches the
        // dialog service (dialog -> profile -> intelligence -> billing -> store), and the window
        // manager reaches the theme service (theme -> window manager).
        services.AddSingleton(provider => new Lazy<IMailDialogService>(provider.GetRequiredService<IMailDialogService>));
        services.AddSingleton(provider => new Lazy<INewThemeService>(provider.GetRequiredService<INewThemeService>));
        services.AddSingleton(provider => new Lazy<IWinoDialogService>(provider.GetRequiredService<IWinoDialogService>));
        // The lock service takes this platform in its constructor, and the PIN dialog reaches the lock service, so the
        // dialog service is only resolved when a prompt opens.
        services.AddSingleton<IAppLockPlatform>(provider => new WindowsAppLockPlatform(
            provider.GetRequiredService<Lazy<IWinoDialogService>>(),
            provider.GetRequiredService<WinUIDispatcher>(),
            () => provider.GetRequiredService<NativeAppService>().GetCoreWindowHwnd?.Invoke() ?? IntPtr.Zero));
        services.AddTransient<INotificationBuilder, NotificationBuilder>();
    }
}
