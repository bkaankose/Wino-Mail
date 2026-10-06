using AppKit;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wino.Authentication;
using Wino.Core;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Platform.MacOS;
using Wino.Platform.MacOS.Security;
using Wino.Platform.MacOS.Services;
using Wino.Services;

namespace Wino.Mail.MacOS;

internal static class Composition
{
    public static ServiceProvider Create(IDispatcher dispatcher, Func<NSWindow?> owner, Action<NSViewController> host, Action<Exception> error)
    {
        // Paths/protection precede database or credential service construction.
        var paths = new MacOSPaths(NSBundle.MainBundle.ResourcePath ?? AppContext.BaseDirectory);
        var newInstallation = paths.IsVerifiedNewInstallation;
        var keychain = new MacOSKeychainStore();
        var protector = new MacOSSecretProtector(keychain);
        if (newInstallation) protector.ProvisionNewInstallationKey(true);
        paths.CreateDirectories();

        var services = new ServiceCollection();
        services.RegisterSharedServices();
        services.RegisterCoreServices();
        services.AddSingleton(dispatcher);
        services.AddSingleton(paths);
        services.AddSingleton(keychain);
        services.AddSingleton<ISecretProtector>(protector);
        services.Replace(ServiceDescriptor.Singleton<IApplicationConfiguration>(new ApplicationConfiguration
        {
            ApplicationDataFolderPath = paths.ApplicationDataRoot,
            PublisherSharedFolderPath = Path.Combine(paths.ApplicationDataRoot, ApplicationConfiguration.SharedFolderName),
            ApplicationTempFolderPath = paths.TemporaryRoot,
            ApplicationDisplayName = "Wino Mail",
            AllowLegacyDataMigration = false,
        }));
        services.AddSingleton<IConfigurationService, MacOSConfigurationService>();
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddSingleton<IApplicationResourceResolver, MacOSApplicationResourceResolver>();
        services.AddSingleton<IFileService, MacOSFileService>();
        services.AddSingleton<IAccountCredentialPersistence, MacOSAccountCredentialPersistence>();
        services.AddSingleton<IDavCredentialStore, MacOSDavCredentialStore>();
        services.AddSingleton<IGoogleTokenStore, MacOSGoogleTokenStore>();
        services.AddSingleton<IAuthenticatorConfig, MailAuthenticatorConfiguration>();
        services.AddSingleton<IOutlookAuthenticationHost>(provider => new MacOSOutlookAuthenticationHost(
            provider.GetRequiredService<IApplicationConfiguration>(), provider.GetRequiredService<IAuthenticatorConfig>(), () => owner() != null));
        services.AddSingleton<IExternalLauncher, MacExternalLauncher>();
        services.AddSingleton<IClipboardService, MacClipboardService>();
        services.AddSingleton<IShortcutPlatformService, MacShortcutPlatformService>();
        services.AddSingleton<IAttachmentPlatformService, MacAttachmentPlatformService>();
        services.AddSingleton<IReaderRuntimeService, MacReaderRuntimeService>();
        services.AddSingleton<IStartupIntegrationService, MacStartupIntegrationService>();
        services.AddSingleton<ISmimeCertificateService, MacSmimeCertificateService>();
        services.AddSingleton<ITaskCompletionSound, MacTaskCompletionSound>();
        services.AddSingleton<IUserPresenceStateProvider, MacUserPresenceStateProvider>();
        services.AddSingleton<INotificationBuilder, MacNotificationBuilder>();
        services.AddSingleton<IStatePersistanceService, MacStatePersistenceService>();
        services.AddSingleton<INewThemeService, MacThemeService>();
        services.AddSingleton<IMicrosoftStoreService, MacMicrosoftStoreService>();
        // Unsupported foundation features are visible in the parity matrix, never counted as complete.
        services.AddSingleton<IPlatformCapabilities>(new PlatformCapabilities());
        services.AddSingleton<IAppMetadataService>(new MacAppMetadataService("0.1.0", MacOSApplicationIdentity.Value,
#if DEBUG
            true
#else
            false
#endif
        ));
        services.AddSingleton<IMailDialogService>(_ => new AppKitDialogService(dispatcher, owner, error));
        services.AddSingleton<IDialogServiceBase>(provider => provider.GetRequiredService<IMailDialogService>());
        services.AddSingleton<IExternalBrowserAuthenticationPresenter>(_ => new AppKitExternalBrowserAuthenticationPresenter(dispatcher, owner, error));
        services.AddSingleton<AppKitNavigationService>(provider => new(provider, dispatcher, host));
        services.AddSingleton<INavigationService>(provider => provider.GetRequiredService<AppKitNavigationService>());
        services.RegisterMacViewModels();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
