using AppKit;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Serilog;
using Wino.Authentication;
using Wino.Core;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Platform.Apple;
using Wino.Platform.Apple.PushRelay;
using Wino.Platform.Apple.Security;
using Wino.Platform.Apple.Services;
using Wino.Platform.MacOS;
using Wino.Platform.MacOS.Services;
using Wino.Services;
using Wino.Services.Dav;
using Wino.Services.Platform;
using Wino.PushRelay.Socket;
using Wino.PushRelay.Socket.KeyStores;
using Wino.Services.PushRelay;
using Wino.Services.Security;

namespace Wino.Mail.MacOS;

internal static class Composition
{
    /// <summary>The bundle identifier. It names the Keychain services and the container folders, so it never changes.</summary>
    private const string BundleIdentifier = "com.winomail.macos";

    public static ServiceProvider Create(IDispatcher dispatcher, Func<NSWindow?> owner, Func<NSViewController, Action?> host, Action<Exception> error)
    {
        // Paths/protection precede database or credential service construction.
        var identity = new AppleApplicationIdentity(BundleIdentifier);
        var paths = new ApplePaths(identity, NSBundle.MainBundle.ResourcePath ?? AppContext.BaseDirectory);
        var newInstallation = paths.IsVerifiedNewInstallation;
        var keychain = new AppleKeychainStore(identity);
        var protector = new AesGcmEnvelopeSecretProtector(new SecureItemSecretProtectionKeyProvider(keychain, identity.Value));
        if (newInstallation) protector.ProvisionNewInstallationKey(true);
        paths.CreateDirectories();

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: false);
        });
        services.RegisterSharedServices();
        services.RegisterCoreServices();
        services.AddSingleton(dispatcher);
        services.AddSingleton(identity);
        services.AddSingleton(paths);
        services.AddSingleton(keychain);
        services.AddSingleton<ISecureItemStore>(keychain);
        services.AddSingleton<ISecretProtector>(protector);
        services.Replace(ServiceDescriptor.Singleton<IApplicationConfiguration>(new ApplicationConfiguration
        {
            ApplicationDataFolderPath = paths.ApplicationDataRoot,
            PublisherSharedFolderPath = Path.Combine(paths.ApplicationDataRoot, ApplicationConfiguration.SharedFolderName),
            ApplicationTempFolderPath = paths.TemporaryRoot,
            ApplicationDisplayName = "Wino Mail",
            AllowLegacyDataMigration = false,
        }));
        services.AddSingleton<IConfigurationService>(_ => new JsonFileConfigurationService(paths.ApplicationDataRoot));
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddSingleton<IApplicationResourceResolver, AppleApplicationResourceResolver>();
        services.AddSingleton<IFileService, FileSystemFileService>();
        services.AddSingleton<IAccountCredentialPersistence, SecureItemAccountCredentialPersistence>();
        services.AddSingleton<IDavCredentialStore, SecureItemDavCredentialStore>();
        services.AddSingleton<IGoogleTokenStore, SecureItemGoogleTokenStore>();
        services.AddSingleton<IAuthenticatorConfig, MailAuthenticatorConfiguration>();
        services.AddSingleton<IOutlookAuthenticationHost>(provider => new MacOSOutlookAuthenticationHost(
            provider.GetRequiredService<IApplicationConfiguration>(), provider.GetRequiredService<IAuthenticatorConfig>(),
            identity, () => owner() != null));
        services.AddSingleton<IExternalLauncher, MacExternalLauncher>();
        services.AddSingleton<IClipboardService, MacClipboardService>();
        services.AddSingleton<IShortcutPlatformService, MacShortcutPlatformService>();
        services.AddSingleton<IAttachmentPlatformService, MacAttachmentPlatformService>();
        services.AddSingleton<IReaderRuntimeService, AppleReaderRuntimeService>();
        services.AddSingleton<IStartupIntegrationService, MacStartupIntegrationService>();
        services.AddSingleton<ISmimeCertificateService, MacSmimeCertificateService>();
        services.AddSingleton<ITaskCompletionSound, MacTaskCompletionSound>();
        services.AddSingleton<IUserPresenceStateProvider, MacUserPresenceStateProvider>();
        services.AddSingleton<INotificationSoundCatalog, MacNotificationSoundCatalog>();
        services.AddSingleton<INotificationImageConverter, MacNotificationImageConverter>();
        services.AddSingleton<IApplicationBadgePresenter, MacDockBadgePresenter>();
        services.AddSingleton<AppleNotificationSounds>();
        services.AddSingleton<INotificationBuilder, AppleNotificationBuilder>();
        services.AddSingleton<IStatePersistanceService, ConfigurationStatePersistenceService>();
        // WinoRelay (opt-in on the Mac): AppDelegate.PushRelay forwards the APNs token and pushes.
        services.AddSingleton<ApplePushTokenProvider>();
        services.AddSingleton<IPushTokenProvider>(provider => provider.GetRequiredService<ApplePushTokenProvider>());
        services.AddSingleton<IPushRelayDeviceIdentityStore, SecureItemPushRelayDeviceIdentityStore>();
        services.AddSingleton<IPushRelayDeviceInfo, MacPushRelayDeviceInfo>();
        // The desktop socket (Wino.PushRelay.Socket): the key lives in the Keychain; nothing starts the socket yet.
        services.AddSingleton<IPushRelaySocketKeyStore, SecureItemPushRelaySocketKeyStore>();
        services.AddWinoRelaySocketClient();
        // The app-level theme service adds the Wino themes (accent + backdrop, custom themes in the Windows file layout) on top of native appearance.
        services.AddSingleton(provider => new Wino.Services.Themes.CustomThemeFileStore(provider.GetRequiredService<IApplicationConfiguration>()));
        services.AddSingleton<MacWinoThemeService>();
        services.AddSingleton<INewThemeService>(provider => provider.GetRequiredService<MacWinoThemeService>());
        services.AddSingleton<IMicrosoftStoreService>(provider => new MacMicrosoftStoreService(provider.GetRequiredService<IConfigurationService>(),
#if WINO_APPSTORE
            isAppStoreBuild: true));
        // StoreKit purchases; WinoAppStorePurchaseService links them to the Wino Account.
        services.AddSingleton<IAppStoreClient, AppleAppStoreClient>();
#else
            isAppStoreBuild: false));
#endif
        // The companion's global shortcut is a Carbon hotkey (MacCompanionHotKeyController).
        services.AddSingleton<MacGlobalHotKeyService>();
        services.AddSingleton<MacCompanionHotKeyController>();
        // The tray is the menu bar icon (MacStatusItemController), so every close behaviour is offered.
        // Additional windows are the pop-out reader, composer and event details windows; the global
        // hotkey is the companion shortcut. The Microsoft Store stays unsupported on macOS.
        services.AddSingleton<IPlatformCapabilities>(new PlatformCapabilities(
            Printing: true, PdfExport: true, Smime: true, AdditionalWindows: true, Notifications: true, NotificationActions: true,
            StartupIntegration: true, Tray: true, GlobalHotkeys: true, GeneralActivation: true,
            // Live notifications need APNs and the user's opt-in (mailbox credentials go through Wino servers).
            RemoteNotifications: true, PushRelayOptIn: true, KeyboardShortcuts: true, FileBackup: true, Purchases: true,
            // The Dock badge launches the app; there is one Dock tile, so the taskbar identity never follows the mode.
            PointerHover: true, MailListDensity: true, PresentationDetection: true, ConfigurableSyncIntervals: true,
            BadgeLaunchNavigation: true,
#if WINO_APPSTORE
            AppleAppStore: true
#else
            AppleAppStore: false
#endif
        ));
        // The bundle's CFBundleShortVersionString is the app version; CFBundleVersion is the build (Sentry dist).
        services.AddSingleton<IAppMetadataService>(new HostAppMetadataService(BundleString("CFBundleShortVersionString"), identity.Value,
#if DEBUG
            true
#else
            false
#endif
            , BundleString("CFBundleVersion"), "Mac"));
        services.AddSingleton<IMailDialogService>(provider => new AppKitDialogService(dispatcher, owner, error, provider));
        services.AddSingleton<IDialogServiceBase>(provider => provider.GetRequiredService<IMailDialogService>());
        services.AddSingleton<IExternalBrowserAuthenticationPresenter>(_ => new AppKitExternalBrowserAuthenticationPresenter(dispatcher, owner, error));
        services.AddLoopbackGoogleAuthorizationCodeReceiver();
        // Wino Account external sign-in. Google runs natively in a web authentication session with the Wino Accounts iOS
        // client. Apple is native in builds with the applesignin entitlement (the csproj defines WINO_APPLE_SIGNIN with
        // it) and goes through the API browser flow, in the same web session, everywhere else.
        var anchors = new MacPresentationAnchorProvider(owner);
        services.AddSingleton(anchors);
        services.AddSingleton<IWinoAccountBrowserAuthBroker>(new AppleWebAuthenticationBroker(dispatcher, anchors, () => anchors.HasAnchor));
        services.AddSingleton<IGoogleNativeSignInProvider>(provider => new GoogleWebSessionSignInProvider(provider.GetRequiredService<IWinoAccountBrowserAuthBroker>()));
#if WINO_APPLE_SIGNIN
        services.AddSingleton<IAppleNativeSignInProvider>(new AppleNativeSignInProvider(dispatcher, anchors));
#endif
        services.AddSingleton<IWinoAccountExternalSignInService>(provider => new WinoAccountExternalSignInService(
            provider.GetRequiredService<IWinoAccountApiClient>(), provider.GetRequiredService<IWinoAccountProfileService>(),
            provider.GetService<IAppleNativeSignInProvider>(), provider.GetRequiredService<IGoogleNativeSignInProvider>(), provider.GetRequiredService<IWinoAccountBrowserAuthBroker>()));
        services.AddSingleton<AppKitNavigationService>(provider => new(provider, dispatcher, host));
        services.AddSingleton<INavigationService>(provider => provider.GetRequiredService<AppKitNavigationService>());
        services.RegisterMacViewModels();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>A string value from the main bundle's Info.plist; empty when missing.</summary>
    private static string BundleString(string key)
        => NSBundle.MainBundle.InfoDictionary?.ObjectForKey(new NSString(key)) is NSString value ? value.ToString() : string.Empty;
}
