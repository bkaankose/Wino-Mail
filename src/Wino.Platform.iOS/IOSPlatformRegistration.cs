using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UIKit;
using Wino.Authentication;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Platform.Apple;
using Wino.Platform.Apple.PushRelay;
using Wino.Platform.Apple.Security;
using Wino.Platform.Apple.Services;
using Wino.Platform.iOS.Services;
using Wino.Services;

namespace Wino.Platform.iOS;

/// <summary>
/// Paths and data protection that must exist before any database or credential service is constructed.
/// <see cref="Create"/> provisions the installation key on a new installation and creates the folders.
/// </summary>
public sealed record IOSPlatformBootstrap(AppleApplicationIdentity Identity, ApplePaths Paths, AppleKeychainStore Keychain, AppleSecretProtector Protector)
{
    public static IOSPlatformBootstrap Create(AppleApplicationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        // Application Support and the temporary folder resolve inside the app container on iOS.
        var paths = new ApplePaths(identity, NSBundle.MainBundle.ResourcePath ?? AppContext.BaseDirectory);
        var newInstallation = paths.IsVerifiedNewInstallation;
        var keychain = new AppleKeychainStore(identity);
        var protector = new AppleSecretProtector(keychain);
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
        services.AddSingleton<IConfigurationService, AppleConfigurationService>();
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddSingleton<IApplicationResourceResolver, AppleApplicationResourceResolver>();
        services.AddSingleton<IFileService, AppleFileService>();
        services.AddSingleton<IAccountCredentialPersistence, AppleAccountCredentialPersistence>();
        services.AddSingleton<IDavCredentialStore, AppleDavCredentialStore>();
        services.AddSingleton<IGoogleTokenStore, AppleGoogleTokenStore>();
        services.AddSingleton<IAuthenticatorConfig, MailAuthenticatorConfiguration>();
        services.AddSingleton<IReaderRuntimeService, AppleReaderRuntimeService>();
        services.AddSingleton<IStatePersistanceService, AppleStatePersistenceService>();
        services.AddSingleton<AppleNotificationSounds>();
        services.AddSingleton<INotificationBuilder, AppleNotificationBuilder>();
        services.AddSingleton<IAppStoreClient, AppleAppStoreClient>();
        services.AddSingleton<IAppMetadataService>(new AppleAppMetadataService(
            BundleString("CFBundleShortVersionString"), identity.Value, IsDebugBuild, BundleString("CFBundleVersion"),
            isPad ? "iPadOS" : "iOS"));

        // iOS implementations.
        services.AddSingleton<INotificationSoundCatalog, IOSNotificationSoundCatalog>();
        services.AddSingleton<INotificationImageConverter, IOSNotificationImageConverter>();
        services.AddSingleton<IApplicationBadgePresenter, IOSApplicationBadgePresenter>();
        services.AddSingleton<IOutlookAuthenticationHost>(provider => new IOSOutlookAuthenticationHost(
            provider.GetRequiredService<IAuthenticatorConfig>(), identity, presenter));
        services.AddSingleton(new IOSPresentationAnchorProvider(presenter));
        services.Replace(ServiceDescriptor.Transient<IGoogleAuthorizationCodeReceiver, IOSGoogleAuthorizationCodeReceiver>());
        services.AddSingleton<IExternalLauncher, IOSExternalLauncher>();
        services.AddSingleton<IClipboardService, IOSClipboardService>();
        services.AddSingleton<IShortcutPlatformService, IOSShortcutPlatformService>();
        services.AddSingleton<IAttachmentPlatformService, IOSAttachmentPlatformService>();
        services.AddSingleton<IStartupIntegrationService, IOSStartupIntegrationService>();
        services.AddSingleton<ISmimeCertificateService, IOSSmimeCertificateService>();
        services.AddSingleton<ITaskCompletionSound, IOSTaskCompletionSound>();
        services.AddSingleton<IUserPresenceStateProvider, IOSUserPresenceStateProvider>();
        services.AddSingleton<IMicrosoftStoreService, IOSMicrosoftStoreService>();

        // WinoRelay: the AppDelegate forwards the APNs token and pushes; the device id stays on this device only.
        services.AddSingleton<ApplePushTokenProvider>();
        services.AddSingleton<IPushTokenProvider>(provider => provider.GetRequiredService<ApplePushTokenProvider>());
        services.AddSingleton<IPushRelayDeviceIdentityStore, AppleKeychainPushRelayDeviceIdentityStore>();
        services.AddSingleton<IPushRelayDeviceInfo>(new IOSPushRelayDeviceInfo(UIDevice.CurrentDevice.Name, UIDevice.CurrentDevice.SystemVersion));

        // Additional windows are iPad scenes (pop-out reader and composer); iPhone has a single scene.
        // Live notifications are always on while a Wino Account is signed in (no opt-in switch on iOS).
        services.AddSingleton<IPlatformCapabilities>(new PlatformCapabilities(
            Printing: true, PdfExport: true, Smime: false, AdditionalWindows: isPad, Notifications: true, NotificationActions: true,
            StartupIntegration: false, Tray: false, GlobalHotkeys: false, GeneralActivation: true, AppleAppStore: true,
            RemoteNotifications: true, PushRelayOptIn: false));
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
