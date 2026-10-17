using Android.App;
using Android.Content;
using Android.Content.PM;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wino.Authentication;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Platform.Android.Authentication;
using Wino.Platform.Android.Background;
using Wino.Platform.Android.Notifications;
using Wino.Platform.Android.PushRelay;
using Wino.Platform.Android.Security;
using Wino.Platform.Android.Services;
using Wino.Services;
using Wino.Services.Dav;
using Wino.Services.Platform;
using Wino.Services.PushRelay;
using Wino.Services.Security;

namespace Wino.Platform.Android;

/// <summary>
/// Paths and data protection that must exist before any database or credential service is constructed.
/// <see cref="Create"/> provisions the installation key on a verified new installation and creates the folders.
/// </summary>
/// <param name="Context">The application context (never an activity).</param>
/// <param name="ApplicationIdentity">The package name; bound into every protected envelope, so it never changes for an installation.</param>
public sealed record AndroidPlatformBootstrap(Context Context, string ApplicationIdentity, AndroidPaths Paths,
    ProtectedFileSecureItemStore SecureItems, AesGcmEnvelopeSecretProtector Protector)
{
    public static AndroidPlatformBootstrap Create(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var application = context.ApplicationContext ?? context;
        var identity = application.PackageName ?? throw new InvalidOperationException("The package name is unavailable.");
        var paths = AndroidPaths.FromContext(application);
        var newInstallation = paths.IsVerifiedNewInstallation;
        // The protection key is wrapped by the Keystore, never kept in the secure item store the protector encrypts.
        var protector = new AesGcmEnvelopeSecretProtector(new AndroidKeystoreProtectionKeyProvider(paths.ProtectionKeyPath, identity));
        if (newInstallation) protector.ProvisionNewInstallationKey(true);
        paths.CreateDirectories();
        return new AndroidPlatformBootstrap(application, identity, paths, new ProtectedFileSecureItemStore(paths.SecureItemsRoot, protector), protector);
    }
}

public static class AndroidPlatformRegistration
{
    /// <summary>
    /// Registers the Android platform services and the shared services the Mac and iOS heads also register. Call it after
    /// <c>RegisterSharedServices</c> and <c>RegisterCoreServices</c>, because it replaces the configuration they register.
    /// It also registers Outlook (MSAL) and Gmail (Play services, else the loopback receiver Core registers) sign-in; the
    /// head forwards <c>OnActivityResult</c> to <see cref="AndroidActivityResults"/> and declares MSAL's redirect activity.
    /// The head still registers the theme, dialog and navigation services, and the notification, background and relay
    /// services.
    /// </summary>
    /// <param name="currentActivity">The started activity to launch other apps from; null while none is started.</param>
    public static IServiceCollection AddAndroidPlatformServices(this IServiceCollection services, Context context, Func<Activity?> currentActivity)
        => services.AddAndroidPlatformServices(AndroidPlatformBootstrap.Create(context), currentActivity);

    /// <summary>
    /// The same registration with paths and protection the head created earlier through
    /// <see cref="AndroidPlatformBootstrap.Create"/>, for example to configure logging before the container.
    /// </summary>
    public static IServiceCollection AddAndroidPlatformServices(this IServiceCollection services, AndroidPlatformBootstrap bootstrap, Func<Activity?> currentActivity)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(currentActivity);

        var context = bootstrap.Context;
        var paths = bootstrap.Paths;

        services.AddSingleton(context);
        services.AddSingleton(paths);
        services.AddSingleton(bootstrap.SecureItems);
        services.AddSingleton<ISecureItemStore>(bootstrap.SecureItems);
        services.AddSingleton<ISecretProtector>(bootstrap.Protector);
        services.Replace(ServiceDescriptor.Singleton<IApplicationConfiguration>(new ApplicationConfiguration
        {
            ApplicationDataFolderPath = paths.ApplicationDataRoot,
            PublisherSharedFolderPath = Path.Combine(paths.ApplicationDataRoot, ApplicationConfiguration.SharedFolderName),
            ApplicationTempFolderPath = paths.TemporaryRoot,
            ApplicationDisplayName = "Wino Mail",
            AllowLegacyDataMigration = false,
        }));

        // Shared services with the same list and lifetimes as the Mac and iOS registrations.
        services.AddSingleton<IConfigurationService>(_ => new JsonFileConfigurationService(paths.ApplicationDataRoot));
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddSingleton<IApplicationResourceResolver>(provider => new FileSystemApplicationResourceResolver(
            paths.PackagedResourceRoot, provider.GetRequiredService<IApplicationConfiguration>()));
        services.AddSingleton<IFileService, FileSystemFileService>();
        services.AddSingleton<IAccountCredentialPersistence, SecureItemAccountCredentialPersistence>();
        services.AddSingleton<IDavCredentialStore, SecureItemDavCredentialStore>();
        services.AddSingleton<IGoogleTokenStore, SecureItemGoogleTokenStore>();
        services.AddSingleton<IAuthenticatorConfig, MailAuthenticatorConfiguration>();
        services.AddSingleton<IStatePersistanceService, ConfigurationStatePersistenceService>();
        services.AddSingleton<IPushRelayDeviceIdentityStore, SecureItemPushRelayDeviceIdentityStore>();
        services.AddSingleton<IAttachmentPlatformService, LauncherAttachmentPlatformService>();
        services.AddSingleton<IStartupIntegrationService, UnavailableStartupIntegrationService>();
        services.AddSingleton<ISmimeCertificateService, UnavailableSmimeCertificateService>();
        services.AddSingleton<IAppMetadataService>(CreateMetadata(context, bootstrap.ApplicationIdentity));

        // Android implementations.
        services.AddSingleton<IDispatcher, AndroidDispatcher>();
        services.AddSingleton<IExternalLauncher>(provider => new AndroidExternalLauncher(
            context, paths, provider.GetRequiredService<IDispatcher>(), currentActivity));
        services.AddSingleton<IClipboardService, AndroidClipboardService>();
        services.AddSingleton<IReaderRuntimeService, AndroidReaderRuntimeService>();
        services.AddSingleton<IShortcutPlatformService, AndroidShortcutPlatformService>();
        services.AddSingleton<IUserPresenceStateProvider, AndroidUserPresenceStateProvider>();
        services.AddSingleton<ITaskCompletionSound, AndroidTaskCompletionSound>();
        services.AddSingleton<IMicrosoftStoreService>(provider => new AndroidMicrosoftStoreService(
            bootstrap.ApplicationIdentity, provider.GetRequiredService<IExternalLauncher>()));

        // Sign-in. Gmail replaces Core's IGmailAuthenticator: Play services AuthorizationClient, with the shared
        // GmailAuthenticator (loopback receiver in a Custom Tab) for stored refresh tokens and devices without Play services.
        services.AddSingleton(provider => new AndroidActivityResults(provider.GetRequiredService<IDispatcher>(), currentActivity));
        services.AddSingleton<IOutlookAuthenticationHost>(provider => new AndroidOutlookAuthenticationHost(
            provider.GetRequiredService<IAuthenticatorConfig>(), context, provider.GetRequiredService<AndroidActivityResults>()));
        services.Replace(ServiceDescriptor.Singleton<IGmailAuthenticator>(provider =>
        {
            var config = provider.GetRequiredService<IAuthenticatorConfig>();
            var tokenStore = provider.GetRequiredService<IGoogleTokenStore>();
            var loopback = new GmailAuthenticator(config, provider.GetRequiredService<IGoogleAuthorizationCodeReceiver>(), tokenStore);
            return new AndroidGmailAuthenticator(context, config, tokenStore, loopback, provider.GetRequiredService<AndroidActivityResults>());
        }));

        // Wino Account external sign-in. Google is native through Credential Manager. Apple has no Android SDK, so it runs
        // the API browser flow in a Custom Tab; the head's WinoAccountAuthRedirectActivity delivers the
        // wino-mail://auth-complete redirect to AndroidExternalAuthCallbacks.
        services.AddSingleton<AndroidExternalAuthCallbacks>();
        services.AddSingleton<IWinoAccountBrowserAuthBroker>(provider => new AndroidBrowserAuthBroker(
            context, provider.GetRequiredService<IDispatcher>(), currentActivity, provider.GetRequiredService<AndroidExternalAuthCallbacks>()));
        services.AddSingleton<IGoogleNativeSignInProvider>(provider => new AndroidGoogleSignInProvider(provider.GetRequiredService<IDispatcher>(), currentActivity));
        services.AddSingleton<IWinoAccountExternalSignInService>(provider => new WinoAccountExternalSignInService(
            provider.GetRequiredService<IWinoAccountApiClient>(), provider.GetRequiredService<IWinoAccountProfileService>(),
            apple: null, provider.GetRequiredService<IGoogleNativeSignInProvider>(), provider.GetRequiredService<IWinoAccountBrowserAuthBroker>()));

        // Relay pushes (compile-only for v1): the FCM token, this device's description; the identity store is above.
        services.AddSingleton(_ => new AndroidFcmTokenProvider(context));
        services.AddSingleton<IPushTokenProvider>(provider => provider.GetRequiredService<AndroidFcmTokenProvider>());
        services.AddSingleton<IPushRelayDeviceInfo, AndroidPushRelayDeviceInfo>();

        // Single-activity app: no additional windows, tray, hotkeys or sign-in startup. Printing and PDF export go
        // through PrintManager. Remote notifications need Firebase, which exists only in builds with google-services.json.
        services.AddSingleton<IPlatformCapabilities>(new PlatformCapabilities(
            Printing: true, PdfExport: true, Smime: false, AdditionalWindows: false, Notifications: true, NotificationActions: true,
            StartupIntegration: false, Tray: false, GlobalHotkeys: false, GeneralActivation: true, MicrosoftStore: false,
            AppleAppStore: false, RemoteNotifications: AndroidFcmTokenProvider.IsFirebaseConfigured(context), PushRelayOptIn: false,
            // No configurable hardware shortcuts yet; backups are Windows and Mac files (paths, not content URIs); no Play
            // Billing in v1, so nothing is sold here.
            KeyboardShortcuts: false, FileBackup: false, Purchases: false,
            // One row density and no hover; the system decides background work, so sync intervals are fixed.
            PointerHover: false, MailListDensity: false, PresentationDetection: false, ConfigurableSyncIntervals: false,
            BadgeLaunchNavigation: false));
        return services;
    }

    /// <summary>
    /// The notification builder, reminder wake-ups and the queued-changes flush. The components that receive taps,
    /// actions, alarms and work live in the head, which owns the container: <paramref name="components"/> (activity and
    /// action receiver), the alarm receiver and the WorkManager worker (a stable Java name, because WorkManager stores it).
    /// </summary>
    public static IServiceCollection AddAndroidNotificationServices(this IServiceCollection services, AndroidNotificationComponents components,
        Type alarmReceiverType, Type workerType)
    {
        ArgumentNullException.ThrowIfNull(components);
        services.AddSingleton(components);
        services.AddSingleton(provider => new AndroidReminderState(provider.GetRequiredService<Context>()));
        services.AddSingleton<AndroidNotificationBuilder>();
        services.Replace(ServiceDescriptor.Singleton<INotificationBuilder>(provider => provider.GetRequiredService<AndroidNotificationBuilder>()));
        services.AddSingleton(provider => new AndroidReminderScheduler(provider.GetRequiredService<Context>(), alarmReceiverType,
            provider.GetRequiredService<ICalendarService>(), provider.GetRequiredService<INotificationBuilder>(), provider.GetRequiredService<AndroidReminderState>()));
        services.AddSingleton(provider => new AndroidPendingChangesScheduler(provider.GetRequiredService<Context>(), workerType,
            provider.GetRequiredService<ISynchronizationManager>()));
        return services;
    }

#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif

    /// <summary>The version name is the app version; the version code is the build (Sentry dist).</summary>
    private static HostAppMetadataService CreateMetadata(Context context, string packageName)
    {
        var manager = context.PackageManager ?? throw new InvalidOperationException("The package manager is unavailable.");
        PackageInfo? info;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            info = manager.GetPackageInfo(packageName, PackageManager.PackageInfoFlags.Of(0L));
        else
            info = manager.GetPackageInfo(packageName, (PackageInfoFlags)0);

        string? versionCode = null;
        if (info is not null)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
                versionCode = info.LongVersionCode.ToString(CultureInfo.InvariantCulture);
            else
                versionCode = info.VersionCode.ToString(CultureInfo.InvariantCulture);
        }
        return new HostAppMetadataService(info?.VersionName ?? string.Empty, packageName, IsDebugBuild, versionCode, "Android");
    }
}
