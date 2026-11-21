using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wino.Authentication;
using Wino.Authentication.OAuth;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Platform.Linux.Authentication;
using Wino.Platform.Linux.Notifications;
using Wino.Platform.Linux.Security;
using Wino.Platform.Linux.Services;
using Wino.Services;
using Wino.Services.Interfaces;
using Wino.Services.Platform;
using Wino.Services.Security;

namespace Wino.Platform.Linux;

/// <summary>
/// Paths and data protection that must exist before any database or credential service is constructed.
/// The protection key lives in the Secret Service and nowhere else; the items it encrypts are files.
/// </summary>
/// <param name="ApplicationIdentity">The application id; bound into every protected envelope, so it never changes for an installation.</param>
public sealed record LinuxPlatformBootstrap(string ApplicationIdentity, LinuxPaths Paths,
    ProtectedFileSecureItemStore SecureItems, AesGcmEnvelopeSecretProtector Protector)
{
    /// <exception cref="SecretServiceException">The Secret Service is missing or stayed locked. Do not continue without it.</exception>
    public static LinuxPlatformBootstrap Create(string applicationIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationIdentity);
        var paths = LinuxPaths.FromEnvironment();
        var newInstallation = paths.IsVerifiedNewInstallation;
        var protector = new AesGcmEnvelopeSecretProtector(new SecureItemSecretProtectionKeyProvider(
            new LibsecretSecureItemStore(applicationIdentity), applicationIdentity));
        if (newInstallation) protector.ProvisionNewInstallationKey(true);
        paths.CreateDirectories();
        return new LinuxPlatformBootstrap(applicationIdentity, paths, new ProtectedFileSecureItemStore(paths.SecureItemsRoot, protector), protector);
    }
}

public static class LinuxPlatformRegistration
{
    /// <summary>
    /// Registers the Linux platform services and the shared services every non-Windows head registers. Call it on the
    /// GTK main thread, after <c>RegisterSharedServices</c> and <c>RegisterCoreServices</c>, because it replaces the
    /// configuration they register. The head still registers navigation, dialogs, theme and lifecycle.
    /// </summary>
    /// <param name="currentWindow">The window that other apps and portal dialogs are opened over; null while none is shown. Read on the main thread.</param>
    public static IServiceCollection AddLinuxPlatformServices(this IServiceCollection services, LinuxPlatformBootstrap bootstrap, Func<Gtk.Window?> currentWindow)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(currentWindow);

        var paths = bootstrap.Paths;
        services.AddSingleton(paths);
        services.AddSingleton(bootstrap.SecureItems);
        services.AddPortablePlatformServices(new LinuxApplicationConfiguration
        {
            ApplicationDataFolderPath = paths.ApplicationDataRoot,
            PublisherSharedFolderPath = Path.Combine(paths.ApplicationDataRoot, ApplicationConfiguration.SharedFolderName),
            ApplicationTempFolderPath = paths.TemporaryRoot,
        }, bootstrap.SecureItems, bootstrap.Protector, CreateMetadata(bootstrap.ApplicationIdentity));

        // Shared services with the same list and lifetimes as the Mac, iOS and Android registrations.
        services.AddSingleton<IApplicationResourceResolver>(provider => new FileSystemApplicationResourceResolver(
            AppContext.BaseDirectory, provider.GetRequiredService<IApplicationConfiguration>()));
        services.AddSingleton<IGoogleTokenStore, SecureItemGoogleTokenStore>();
        services.AddSingleton<IOAuthTokenStore, SecureItemOAuthTokenStore>();
        services.AddSingleton<IAuthenticatorConfig, MailAuthenticatorConfiguration>();
        services.AddSingleton<IAttachmentPlatformService, LauncherAttachmentPlatformService>();
        services.AddSingleton<IStartupIntegrationService, UnavailableStartupIntegrationService>();
        services.AddSingleton<ISmimeCertificateService, UnavailableSmimeCertificateService>();
        services.AddSingleton<IAppLockPlatform, UnavailableAppLockPlatform>();

        // Linux implementations.
        var dispatcher = new GtkDispatcher();
        services.AddSingleton(dispatcher);
        services.AddSingleton<IDispatcher>(dispatcher);
        services.AddSingleton<IExternalLauncher>(provider => new LinuxExternalLauncher(provider.GetRequiredService<IDispatcher>(), currentWindow));
        services.AddSingleton<IClipboardService, LinuxClipboardService>();
        services.AddSingleton<IReaderRuntimeService, LinuxReaderRuntimeService>();
        services.AddSingleton<LinuxShortcutPlatformService>();
        services.AddSingleton<IShortcutPlatformService>(provider => provider.GetRequiredService<LinuxShortcutPlatformService>());
        services.AddSingleton<IUserPresenceStateProvider, LinuxUserPresenceStateProvider>();
        services.AddSingleton<ITaskCompletionSound, LinuxTaskCompletionSound>();
        services.AddSingleton<IMicrosoftStoreService, LinuxStoreService>();
        services.AddSingleton<IUnderlyingThemeService, LinuxUnderlyingThemeService>();
        services.AddSingleton<IMicrophoneService, LinuxMicrophoneService>();
        services.Replace(ServiceDescriptor.Singleton<INotificationBuilder, LinuxNotificationBuilder>());
        services.Replace(ServiceDescriptor.Singleton<IInstalledBrowserService, LinuxInstalledBrowserService>());

        // Browser sign-in: wino://auth-complete callbacks, the custom-scheme OAuth broker and the Wino Account broker;
        // generic OAuth with a loopback redirect uses the shared receiver, Outlook MSAL with the system browser, and the
        // head supplies the embedded browser. Gmail uses the loopback receiver that AddGmailProvider registers (the desktop
        // flow, as Android's fallback): the default browser, http://127.0.0.1:<port>/authorize/, PKCE. Nothing is added for it.
        services.AddSingleton<LinuxExternalAuthCallbacks>();
        services.AddSingleton<IOAuthBrowserBroker>(provider => new LinuxOAuthBrowserBroker(
            provider.GetRequiredService<IExternalLauncher>(), provider.GetRequiredService<LinuxExternalAuthCallbacks>(),
            () => provider.GetService<IExternalBrowserAuthenticationPresenter>()));
        services.AddSingleton<IWinoAccountBrowserAuthBroker>(provider => new LinuxWinoAccountBrowserAuthBroker(
            provider.GetRequiredService<IExternalLauncher>(), provider.GetRequiredService<LinuxExternalAuthCallbacks>()));
        services.AddSingleton<LinuxWindowPresence>();
        services.AddSingleton<IOutlookAuthenticationHost>(provider =>
        {
            var presence = provider.GetRequiredService<LinuxWindowPresence>();
            return new LinuxOutlookAuthenticationHost(provider.GetRequiredService<IAuthenticatorConfig>(), provider.GetRequiredService<ISecureItemStore>(),
                provider.GetRequiredService<IExternalLauncher>(), () => provider.GetService<IExternalBrowserAuthenticationPresenter>(),
                () => presence.HasWindow, SignInTimeout());
        });
        services.AddSingleton<IWinoAccountExternalSignInService>(provider => new WinoAccountExternalSignInService(
            provider.GetRequiredService<IWinoAccountApiClient>(), provider.GetRequiredService<IWinoAccountProfileService>(),
            apple: null, google: null, provider.GetRequiredService<IWinoAccountBrowserAuthBroker>()));

        // No relay device info, push token or attestation is registered, so live notifications report an unsupported platform.
        // One capability per line, in alphabetical order, so that parallel changes merge.
        services.AddSingleton<IPlatformCapabilities>(new PlatformCapabilities(
            AdditionalWindows: true,
            AddressFirstAccountSetup: true,
            ConfigurableSyncIntervals: true,
            FileBackup: true,
            GeneralActivation: true,
            KeyboardShortcuts: true,
            MailListDensity: true,
            Notifications: true,
            OAuthLoopbackRedirect: true,
            PdfExport: true,
            PointerHover: true,
            Printing: true));
        return services;
    }

    private static TimeSpan SignInTimeout()
    {
#if DEBUG
        if (int.TryParse(Environment.GetEnvironmentVariable("WINO_DEBUG_SIGNIN_TIMEOUT_SECONDS"), out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
#endif
        return TimeSpan.FromMinutes(5);
    }

#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif

    private static HostAppMetadataService CreateMetadata(string applicationIdentity)
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(LinuxPlatformRegistration).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "0.0.0";
        return new HostAppMetadataService(version, applicationIdentity, IsDebugBuild, null, "Linux");
    }
}
