using System;
using Microsoft.Extensions.DependencyInjection;
using Wino.Authentication;
using Wino.Authentication.OAuth;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Services;
using Wino.Mail.WinUI.Services.WidgetsAndTiles;
using Wino.Platform.Windows.Authentication;
using Wino.Platform.Windows.PushRelay;
using Wino.Platform.Windows.Services;
using Wino.Services;

namespace Wino.Platform.Windows;

public static class WindowsPlatformRegistration
{
    /// <summary>
    /// Registers the Windows operating-system services this project implements. The head constructs
    /// <paramref name="nativeAppService"/> on its UI thread, because the service keeps that thread's queue, and
    /// registers <see cref="IWindowsMainWindowHost"/>, the dialog, theme, window and navigation services, the
    /// credential storage and the notification builder.
    /// </summary>
    public static IServiceCollection AddWindowsPlatformServices(this IServiceCollection services, NativeAppService nativeAppService)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(nativeAppService);

        services.AddSingleton<IUnderlyingThemeService, UnderlyingThemeService>();
        services.AddSingleton(nativeAppService);
        services.AddSingleton<INativeAppService>(provider => provider.GetRequiredService<NativeAppService>());
        services.AddSingleton<IExternalLauncher>(provider => provider.GetRequiredService<NativeAppService>());
        services.AddSingleton<IClipboardService>(provider => provider.GetRequiredService<NativeAppService>());
        services.AddSingleton<IShortcutPlatformService>(provider => provider.GetRequiredService<NativeAppService>());
        services.AddSingleton<ITaskCompletionSound>(provider => provider.GetRequiredService<NativeAppService>());
        services.AddSingleton<IWindowsAttachmentPolicyService, WindowsAttachmentPolicyService>();
        services.AddSingleton<IAttachmentPlatformService, WindowsAttachmentPlatformService>();
        services.AddSingleton<IAppMetadataService>(provider => provider.GetRequiredService<NativeAppService>());
        services.AddSingleton<IUserPresenceStateProvider>(provider => provider.GetRequiredService<NativeAppService>());
        services.AddSingleton<ContactPictureThumbnailer>();
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<IApplicationResourceResolver, WindowsApplicationResourceResolver>();
        services.AddSingleton<IStartupIntegrationService, WindowsStartupIntegrationService>();
        services.AddSingleton<IReaderRuntimeService, WindowsReaderRuntimeService>();
        services.AddSingleton<IInstalledBrowserService, WindowsInstalledBrowserService>();
        services.AddSingleton<IMicrophoneService, WindowsMicrophoneService>();
        services.AddSingleton<IPushRelayDeviceIdentityStore, WindowsPushRelayDeviceIdentityStore>();
        services.AddSingleton<IPushRelayDeviceInfo, WindowsPushRelayDeviceInfo>();
        services.AddSingleton<IPushTokenProvider, NullPushTokenProvider>();
        services.AddSingleton<IDeviceAttestationProvider, WindowsBoundKeyAttestationProvider>();
        services.AddSingleton<IOutlookAuthenticationHost>(provider => new WindowsOutlookAuthenticationHost(
            provider.GetRequiredService<IApplicationConfiguration>(),
            provider.GetRequiredService<IAuthenticatorConfig>(),
            () => provider.GetRequiredService<NativeAppService>().GetCoreWindowHwnd?.Invoke() ?? IntPtr.Zero));
        services.AddSingleton<ISmimeCertificateService, SmimeCertificateService>();

        // Wino Account Sign in with Microsoft, Google and Apple: all through the API's browser flow. The wino://auth-complete
        // protocol activation (App.WinoAccountAuthActivation) delivers the redirect to the waiting broker.
        services.AddSingleton<WindowsExternalAuthCallbacks>();
        services.AddSingleton<IWinoAccountBrowserAuthBroker>(provider => new WindowsBrowserAuthBroker(
            provider.GetRequiredService<IExternalLauncher>(), provider.GetRequiredService<WindowsExternalAuthCallbacks>()));
        // IMAP and SMTP OAuth sign-in: wino://auth-complete/oauth callbacks complete only this broker's wait.
        services.AddSingleton<IOAuthBrowserBroker>(provider => new WindowsOAuthBrowserBroker(
            provider.GetRequiredService<IExternalLauncher>(), provider.GetRequiredService<WindowsExternalAuthCallbacks>(),
            () => provider.GetService<IExternalBrowserAuthenticationPresenter>()));

        services.AddTransient<IConfigurationService, ConfigurationService>();
        services.AddTransient<IFileService, FileService>();
        services.AddSingleton<IMicrosoftStoreService, MicrosoftStoreService>();
        services.AddSingleton<INotificationHostClient, NotificationHostClient>();

        return services;
    }
}
