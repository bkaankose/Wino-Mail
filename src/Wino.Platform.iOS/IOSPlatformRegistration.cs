using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UIKit;
using Wino.Authentication;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Platform;
using Wino.Platform.Apple;
using Wino.Platform.Apple.PushRelay;
using Wino.Platform.Apple.Security;
using Wino.Platform.Apple.Services;
using Wino.Platform.iOS.Services;
using Wino.Services;
using Wino.Services.Dav;
using Wino.Services.Platform;
using Wino.Services.PushRelay;
using Wino.Services.Security;

namespace Wino.Platform.iOS;

/// <summary>
/// Paths and data protection that must exist before any database or credential service is constructed.
/// <see cref="Create"/> provisions the installation key on a new installation and creates the folders.
/// </summary>
public sealed record IOSPlatformBootstrap(AppleApplicationIdentity Identity, ApplePaths Paths, AppleKeychainStore Keychain, AesGcmEnvelopeSecretProtector Protector)
{
    public static IOSPlatformBootstrap Create(AppleApplicationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        // Application Support and the temporary folder resolve inside the app container on iOS.
        var paths = new ApplePaths(identity, NSBundle.MainBundle.ResourcePath ?? AppContext.BaseDirectory);
        var newInstallation = paths.IsVerifiedNewInstallation;
        var keychain = new AppleKeychainStore(identity);
        var protector = new AesGcmEnvelopeSecretProtector(new SecureItemSecretProtectionKeyProvider(keychain, identity.Value));
        if (newInstallation) protector.ProvisionNewInstallationKey(true);
        paths.CreateDirectories();
        return new IOSPlatformBootstrap(identity, paths, keychain, protector);
    }
}

public static class IOSPlatformRegistration
{
    /// <summary>
    /// Registers the iOS platform services and the Apple services the Mac head also registers. Call it on the main
    /// thread (it reads the device idiom) after <c>RegisterSharedServices</c> and <c>RegisterCoreServices</c>, because it
    /// replaces the configuration and the Google authorization receiver those register. The head still registers
    /// <see cref="IDispatcher"/>, the theme, dialog and navigation services, and optionally an <see cref="IIOSFilePresenter"/>.
    /// </summary>
    /// <param name="presenter">The view controller to present sign-in sessions from; null while none is on screen.</param>
    public static IServiceCollection AddIOSPlatformServices(this IServiceCollection services, AppleApplicationIdentity identity, Func<UIViewController?> presenter)
        => services.AddIOSPlatformServices(IOSPlatformBootstrap.Create(identity), presenter);

    /// <summary>
    /// The same registration with paths and protection the head created earlier through
    /// <see cref="IOSPlatformBootstrap.Create"/>, for example to configure logging before the container.
    /// </summary>
    public static IServiceCollection AddIOSPlatformServices(this IServiceCollection services, IOSPlatformBootstrap bootstrap, Func<UIViewController?> presenter)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(presenter);

        var identity = bootstrap.Identity;
        var paths = bootstrap.Paths;
        var isPad = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad;

        services.AddSingleton(identity);
        services.AddSingleton(paths);
        services.AddSingleton(bootstrap.Keychain);
        services.AddSingleton<ISecureItemStore>(bootstrap.Keychain);
        services.AddSingleton<ISecretProtector>(bootstrap.Protector);
        services.Replace(ServiceDescriptor.Singleton<IApplicationConfiguration>(new ApplicationConfiguration
        {
            ApplicationDataFolderPath = paths.ApplicationDataRoot,
            PublisherSharedFolderPath = Path.Combine(paths.ApplicationDataRoot, ApplicationConfiguration.SharedFolderName),
            ApplicationTempFolderPath = paths.TemporaryRoot,
            ApplicationDisplayName = "Wino Mail",
            AllowLegacyDataMigration = false,
        }));

        // Apple services shared with the Mac head (same list and lifetimes as its Composition).
        services.AddSingleton<IConfigurationService>(_ => new JsonFileConfigurationService(paths.ApplicationDataRoot));
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddSingleton<IApplicationResourceResolver, AppleApplicationResourceResolver>();
        services.AddSingleton<IFileService, FileSystemFileService>();
        services.AddSingleton<IAccountCredentialPersistence, SecureItemAccountCredentialPersistence>();
        services.AddSingleton<IDavCredentialStore, SecureItemDavCredentialStore>();
        services.AddSingleton<IGoogleTokenStore, SecureItemGoogleTokenStore>();
        // Gmail mailboxes sign in with the Wino Mail iOS client in the web authentication sheet; Windows keeps the UWP client.
        services.AddSingleton<IAuthenticatorConfig>(provider => new MailAuthenticatorConfiguration(
            provider.GetService<IApplicationConfiguration>(), WinoAccountExternalAuth.GoogleAppleClientId));
        services.AddSingleton<IReaderRuntimeService, AppleReaderRuntimeService>();
        services.AddSingleton<IStatePersistanceService, ConfigurationStatePersistenceService>();
        services.AddSingleton<AppleNotificationSounds>();
        services.AddSingleton<INotificationSoundProvider>(provider => provider.GetRequiredService<AppleNotificationSounds>());
        services.AddSingleton<INotificationBuilder, AppleNotificationBuilder>();
        services.AddSingleton<IAppStoreClient, AppleAppStoreClient>();
        services.AddSingleton<IAppMetadataService>(new HostAppMetadataService(
            BundleString("CFBundleShortVersionString"), identity.Value, IsDebugBuild, BundleString("CFBundleVersion"),
            isPad ? "iPadOS" : "iOS"));

        // iOS implementations.
        services.AddSingleton<INotificationSoundCatalog, IOSNotificationSoundCatalog>();
        services.AddSingleton<INotificationImageConverter, IOSNotificationImageConverter>();
        services.AddSingleton<IApplicationBadgePresenter, IOSApplicationBadgePresenter>();
        services.AddSingleton<IOutlookAuthenticationHost>(provider => new IOSOutlookAuthenticationHost(
            provider.GetRequiredService<IAuthenticatorConfig>(), identity, presenter));
        var anchors = new IOSPresentationAnchorProvider(presenter);
        services.AddSingleton(anchors);
        // Wino Account external sign-in, both native: Apple through AuthenticationServices (the entitlement is in both
        // iOS entitlements files), Google through a web authentication session with the Wino Accounts iOS client. The
        // browser broker also serves the API browser flow should a provider ever need it.
        services.AddSingleton<IWinoAccountBrowserAuthBroker>(provider => new AppleWebAuthenticationBroker(provider.GetRequiredService<IDispatcher>(), anchors, () => anchors.HasAnchor));
        services.Replace(ServiceDescriptor.Transient<IGoogleAuthorizationCodeReceiver>(provider => new WebSessionGoogleAuthorizationCodeReceiver(
            provider.GetRequiredService<IAuthenticatorConfig>(), provider.GetRequiredService<IWinoAccountBrowserAuthBroker>())));
        services.AddSingleton<IAppleNativeSignInProvider>(provider => new AppleNativeSignInProvider(provider.GetRequiredService<IDispatcher>(), anchors));
        services.AddSingleton<IGoogleNativeSignInProvider>(provider => new GoogleWebSessionSignInProvider(provider.GetRequiredService<IWinoAccountBrowserAuthBroker>()));
        services.AddSingleton<IWinoAccountExternalSignInService>(provider => new WinoAccountExternalSignInService(
            provider.GetRequiredService<IWinoAccountApiClient>(), provider.GetRequiredService<IWinoAccountProfileService>(),
            provider.GetRequiredService<IAppleNativeSignInProvider>(), provider.GetRequiredService<IGoogleNativeSignInProvider>(), provider.GetRequiredService<IWinoAccountBrowserAuthBroker>()));
        services.AddSingleton<IExternalLauncher, IOSExternalLauncher>();
        services.AddSingleton<IClipboardService, IOSClipboardService>();
        services.AddSingleton<IShortcutPlatformService, IOSShortcutPlatformService>();
        services.AddSingleton<IAttachmentPlatformService, LauncherAttachmentPlatformService>();
        services.AddSingleton<IStartupIntegrationService, UnavailableStartupIntegrationService>();
        services.AddSingleton<ISmimeCertificateService, UnavailableSmimeCertificateService>();
        services.AddSingleton<ITaskCompletionSound, IOSTaskCompletionSound>();
        services.AddSingleton<IUserPresenceStateProvider, IOSUserPresenceStateProvider>();
        services.AddSingleton<IMicrosoftStoreService, IOSMicrosoftStoreService>();

        // WinoRelay: the AppDelegate forwards the APNs token and pushes; the device id stays on this device only.
        services.AddSingleton<ApplePushTokenProvider>();
        services.AddSingleton<IPushTokenProvider>(provider => provider.GetRequiredService<ApplePushTokenProvider>());
        services.AddSingleton<IPushRelayDeviceIdentityStore, SecureItemPushRelayDeviceIdentityStore>();
        services.AddSingleton<IPushRelayDeviceInfo>(new IOSPushRelayDeviceInfo(UIDevice.CurrentDevice.Name, UIDevice.CurrentDevice.SystemVersion));
        // App Attest proves the genuine app before the Wino API issues the relay device credential (not in the Simulator).
        services.AddSingleton<IDeviceAttestationProvider>(provider => new PushRelay.IOSAppAttestProvider(provider.GetRequiredService<ISecureItemStore>()));
        // The Notification Service Extension reads this Keychain directory to turn a relay cid into a provider and address.
        services.AddSingleton<PushRelay.RelayExtensionAccountDirectory>();
        services.AddSingleton<PushRelay.RelayExtensionPictureMirror>();

        // Additional windows are iPad scenes (pop-out reader and composer); iPhone has a single scene.
        // Live notifications are always on while a Wino Account is signed in (no opt-in switch on iOS).
        services.AddSingleton<IPlatformCapabilities>(new PlatformCapabilities(
            Printing: true, PdfExport: true, Smime: false, AdditionalWindows: isPad, Notifications: true, NotificationActions: true,
            StartupIntegration: false, Tray: false, GlobalHotkeys: false, GeneralActivation: true, AppleAppStore: true,
            RemoteNotifications: true,
            // Hardware keyboard shortcuts are an iPad feature; backups are Windows and Mac files.
            KeyboardShortcuts: isPad, FileBackup: false, Purchases: true,
            // One row density and no hover; the system schedules background refresh, so sync intervals are fixed.
            PointerHover: false, MailListDensity: false, PresentationDetection: false, ConfigurableSyncIntervals: false,
            BadgeLaunchNavigation: false));
        return services;
    }

#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif

    /// <summary>A string value from the main bundle's Info.plist; empty when missing.</summary>
    private static string BundleString(string key)
        => NSBundle.MainBundle.InfoDictionary?.ObjectForKey(new NSString(key)) is NSString value ? value.ToString() : string.Empty;
}
