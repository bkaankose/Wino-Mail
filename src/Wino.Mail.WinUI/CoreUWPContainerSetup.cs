using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Services;
using Wino.Services;
using Wino.Platform.Windows;
using Wino.Platform.Windows.Services;
using Wino.Authentication;
using Wino.Core.Domain.Models.Platform;

namespace Wino.Mail.WinUI;

public static class CoreUWPContainerSetup
{
    public static void RegisterCoreUWPServices(this IServiceCollection services)
    {
        services.AddSingleton<IApplicationResourceManager<ResourceDictionary>, ApplicationResourceManager>();
        services.AddSingleton<WinUIDispatcher>();
        services.AddSingleton<IDispatcher>(provider => provider.GetRequiredService<WinUIDispatcher>());

        services.AddSingleton<IUnderlyingThemeService, UnderlyingThemeService>();
        services.AddSingleton<IWinoWindowManager, WinoWindowManager>();
        // Capture the host UI dispatcher before background initialization resolves it.
        services.AddSingleton(new NativeAppService());
        services.AddSingleton<INativeAppService>(provider => provider.GetRequiredService<NativeAppService>());
        services.AddSingleton<IExternalLauncher>(provider => provider.GetRequiredService<NativeAppService>());
        services.AddSingleton<IClipboardService>(provider => provider.GetRequiredService<NativeAppService>());
        services.AddSingleton<IShortcutPlatformService>(provider => provider.GetRequiredService<NativeAppService>());
        services.AddSingleton<ITaskCompletionSound>(provider => provider.GetRequiredService<NativeAppService>());
        services.AddSingleton<IWindowsAttachmentPolicyService, WindowsAttachmentPolicyService>();
        services.AddSingleton<IAttachmentPlatformService, WindowsAttachmentPlatformService>();
        services.AddSingleton<IAppMetadataService>(provider => provider.GetRequiredService<NativeAppService>());
        services.AddSingleton<IUserPresenceStateProvider>(provider => provider.GetRequiredService<NativeAppService>());
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<IAccountCredentialPersistence, WindowsAccountCredentialPersistence>();
        services.AddSingleton<IApplicationResourceResolver, WindowsApplicationResourceResolver>();
        services.AddSingleton<IStartupIntegrationService, WindowsStartupIntegrationService>();
        services.AddSingleton<IReaderRuntimeService, WindowsReaderRuntimeService>();
        services.AddSingleton<IPlatformCapabilities>(_ => new PlatformCapabilities(
            Printing: Windows.Graphics.Printing.PrintManager.IsSupported(), PdfExport: true,
            Smime: true, AdditionalWindows: true, Notifications: true, NotificationActions: true,
            StartupIntegration: true, Tray: true, GlobalHotkeys: true, GeneralActivation: true,
            MicrosoftStore: true));
        services.AddSingleton<IOutlookAuthenticationHost>(provider => new WindowsOutlookAuthenticationHost(
            provider.GetRequiredService<IApplicationConfiguration>(),
            provider.GetRequiredService<IAuthenticatorConfig>(),
            () => provider.GetRequiredService<NativeAppService>().GetCoreWindowHwnd?.Invoke() ?? IntPtr.Zero));
        services.AddSingleton<IGoogleTokenStore, WindowsGoogleTokenStore>();
        services.AddSingleton<INewThemeService, NewThemeService>();
        services.AddSingleton<IStatePersistanceService, StatePersistenceService>();
        services.AddSingleton<ISmimeCertificateService, SmimeCertificateService>();

        // One dialog stack, one presentation semaphore: the base interface forwards to the mail dialog service.
        services.AddSingleton<IDialogServiceBase>(provider => provider.GetRequiredService<IMailDialogService>());
        services.AddSingleton<IExternalBrowserAuthenticationPresenter, ExternalBrowserAuthenticationPresenter>();
        services.AddTransient<IConfigurationService, ConfigurationService>();
        services.AddTransient<IFileService, FileService>();
        services.AddSingleton<IMicrosoftStoreService, MicrosoftStoreService>();
        // Lazy edges break the two constructor cycles in the shell: the Store service reaches the
        // dialog service (dialog -> profile -> intelligence -> billing -> store), and the window
        // manager reaches the theme service (theme -> window manager).
        services.AddSingleton(provider => new Lazy<IMailDialogService>(provider.GetRequiredService<IMailDialogService>));
        services.AddSingleton(provider => new Lazy<INewThemeService>(provider.GetRequiredService<INewThemeService>));
        services.AddSingleton<INotificationHostClient, NotificationHostClient>();
        services.AddTransient<INotificationBuilder, NotificationBuilder>();
        services.AddSingleton<ICalendarReminderServer, CalendarReminderServer>();
        services.AddSingleton<IWindowsPrintService, PrintService>();

    }

    public static void RegisterCoreViewModels(this IServiceCollection services)
    {
        services.AddTransient(typeof(SettingsDialogViewModel));
        services.AddTransient(typeof(PersonalizationPageViewModel));
        services.AddTransient(typeof(ApplicationThemeGalleryPageViewModel));
        services.AddTransient(typeof(ApplicationThemeEditorPageViewModel));
        services.AddTransient(typeof(SettingOptionsPageViewModel));
        services.AddTransient(typeof(AboutPageViewModel));
        services.AddTransient(typeof(SettingsPageViewModel));
        services.AddTransient(typeof(WelcomeHostPageViewModel));
        services.AddTransient(typeof(KeyboardShortcutsPageViewModel));
        services.AddTransient(typeof(WhatsNewPageViewModel));
    }
}


